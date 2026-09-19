using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StardewValley.GameData.Crops;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewValley.WebPlatform.Wiki;

/// <summary>
/// Feeds the wiki side panel (wwwroot/js/wikiPanel.js): works out what the player is focused on
/// (hovered item, held item, villager being talked to) and sends a card of facts read from the
/// game's own data. The panel adds a short wiki summary and a link for the same subject.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class WikiContext
{
	private const string Module = "wikiPanel";

	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

	[JSImport("init", Module)] private static partial void Init();
	[JSImport("show", Module)] private static partial void Show(string cardJson);
	[JSImport("isOpen", Module)] private static partial bool IsOpen();

	private static bool available;
	private static DateTime lastPoll;
	private static string lastKey;
	private static bool reportedError;

	// Menus keep the item under the cursor in a field with one of these names.
	private static readonly string[] HoverFieldNames = { "hoveredItem", "hoverItem" };
	private static readonly Dictionary<Type, FieldInfo> hoverFields = new();

	public static async Task InitializeAsync(string moduleUrl)
	{
		try
		{
			await JSHost.ImportAsync(Module, moduleUrl);
			Init();
			available = true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[port] Wiki panel unavailable: " + ex.Message);
		}
	}

	/// <summary>Called every frame; checks focus a few times a second and only sends changes.</summary>
	public static void Update()
	{
		if (!available || DateTime.UtcNow - lastPoll < PollInterval || !IsOpen())
		{
			return;
		}
		lastPoll = DateTime.UtcNow;
		try
		{
			object focus = FindFocus();
			string key = focus switch
			{
				Item item => "item:" + item.QualifiedItemId,
				NPC npc => "npc:" + npc.Name,
				_ => null,
			};
			// Keep showing the last subject when nothing is focused.
			if (key == null || key == lastKey)
			{
				return;
			}
			lastKey = key;
			JsonObject card = focus is Item i ? ItemCard(i) : NpcCard((NPC)focus);
			card["key"] = key;
			Show(card.ToJsonString());
		}
		catch (Exception ex)
		{
			if (!reportedError)
			{
				reportedError = true;
				Console.Error.WriteLine("[port] Wiki panel: couldn't read game state: " + ex);
			}
		}
	}

	// ---------- focus ----------

	private static object FindFocus()
	{
		if (Game1.player == null || !Game1.hasLoadedGame || Game1.currentLocation == null)
		{
			return null;
		}
		IClickableMenu menu = Game1.activeClickableMenu;
		if (menu is GameMenu gameMenu)
		{
			menu = gameMenu.GetCurrentPage();
		}
		if (menu != null)
		{
			if (Hovered(menu) is Item hovered)
			{
				return hovered;
			}
			if (menu is DialogueBox && Game1.currentSpeaker != null)
			{
				return Game1.currentSpeaker;
			}
			if (menu is ProfileMenu profile && profile.Current?.Character is NPC profiled)
			{
				return profiled;
			}
			return null;
		}
		foreach (IClickableMenu onScreen in Game1.onScreenMenus)
		{
			if (onScreen is Toolbar toolbar && toolbar.hoverItem != null)
			{
				return toolbar.hoverItem;
			}
		}
		return Game1.player.CurrentItem;
	}

	private static object Hovered(IClickableMenu menu)
	{
		Type type = menu.GetType();
		if (!hoverFields.TryGetValue(type, out FieldInfo field))
		{
			field = HoverFieldNames
				.Select(name => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				.FirstOrDefault(f => f != null && (typeof(Item).IsAssignableFrom(f.FieldType) || typeof(ISalable).IsAssignableFrom(f.FieldType)));
			hoverFields[type] = field;
		}
		return field?.GetValue(menu) as Item;
	}

	// ---------- cards ----------

	internal static JsonObject ItemCard(Item item)
	{
		var facts = new JsonArray();
		var sections = new JsonArray();

		string category = item.getCategoryName();
		int price = item.sellToStorePrice();
		if (price > 0)
		{
			facts.Add(Fact("Sells for", $"{price:N0}g"));
		}

		AddCropFacts(item, facts);
		AddFishFacts(item, facts);

		if (item is StardewValley.Object obj && obj.canBeGivenAsGift())
		{
			AddGiftTastes(item, sections);
		}
		AddBundleNeeds(item, sections);

		return new JsonObject
		{
			["kind"] = "item",
			["title"] = item.DisplayName,
			["subtitle"] = string.IsNullOrEmpty(category) ? null : category,
			["description"] = SafeDescription(item),
			// Internal names are English and match wiki page titles for almost everything.
			["wiki"] = item.Name,
			["facts"] = facts,
			["sections"] = sections,
		};
	}

	private static void AddCropFacts(Item item, JsonArray facts)
	{
		if (Game1.cropData == null)
		{
			return;
		}
		// Seed -> its crop.
		if (item.TypeDefinitionId == "(O)" && Game1.cropData.TryGetValue(item.ItemId, out CropData seedCrop))
		{
			facts.Add(Fact("Grows in", Seasons(seedCrop)));
			facts.Add(Fact("Days to grow", seedCrop.DaysInPhase.Sum().ToString()));
			if (seedCrop.RegrowDays > 0)
			{
				facts.Add(Fact("Regrows every", $"{seedCrop.RegrowDays} days"));
			}
			if (ItemRegistry.GetData(seedCrop.HarvestItemId) is ParsedItemData harvest)
			{
				facts.Add(Fact("Produces", harvest.DisplayName));
			}
			return;
		}
		// Harvest -> the seed that grows it.
		foreach (var (seedId, crop) in Game1.cropData)
		{
			if (crop.HarvestItemId != null && ItemRegistry.QualifyItemId(crop.HarvestItemId) == item.QualifiedItemId)
			{
				string seedName = ItemRegistry.GetData(seedId)?.DisplayName ?? seedId;
				facts.Add(Fact("Grown from", seedName));
				facts.Add(Fact("Season", Seasons(crop)));
				facts.Add(Fact("Days to grow", crop.DaysInPhase.Sum() + (crop.RegrowDays > 0 ? $" (regrows every {crop.RegrowDays})" : "")));
				return;
			}
		}
	}

	private static void AddFishFacts(Item item, JsonArray facts)
	{
		if (item.Category != StardewValley.Object.FishCategory || item.TypeDefinitionId != "(O)")
		{
			return;
		}
		if (!DataLoader.Fish(Game1.content).TryGetValue(item.ItemId, out string raw))
		{
			return;
		}
		string[] f = raw.Split('/');
		if (f.Length > 1 && f[1] == "trap")
		{
			facts.Add(Fact("Caught with", "Crab pot"));
			return;
		}
		if (f.Length > 7)
		{
			facts.Add(Fact("Time", FishTimes(f[5])));
			facts.Add(Fact("Season", string.Join(", ", ArgUtility.SplitBySpace(f[6]).Select(Capitalize))));
			facts.Add(Fact("Weather", f[7] switch { "sunny" => "Sun", "rainy" => "Rain", _ => "Any" }));
		}
	}

	private static void AddGiftTastes(Item item, JsonArray sections)
	{
		var buckets = new SortedDictionary<int, List<string>>();
		Utility.ForEachVillager(npc =>
		{
			if (npc.CanReceiveGifts())
			{
				int taste = npc.getGiftTasteForThisItem(item);
				if (!buckets.TryGetValue(taste, out List<string> names))
				{
					buckets[taste] = names = new List<string>();
				}
				names.Add(npc.displayName);
			}
			return true;
		});
		AddTasteSection(sections, "Loved by", buckets, NPC.gift_taste_love);
		AddTasteSection(sections, "Liked by", buckets, NPC.gift_taste_like);
		AddTasteSection(sections, "Disliked by", buckets, NPC.gift_taste_dislike);
		AddTasteSection(sections, "Hated by", buckets, NPC.gift_taste_hate);
	}

	private static void AddTasteSection(JsonArray sections, string title, SortedDictionary<int, List<string>> buckets, int taste)
	{
		if (buckets.TryGetValue(taste, out List<string> names) && names.Count > 0)
		{
			names.Sort(StringComparer.CurrentCultureIgnoreCase);
			sections.Add(Section(title, names));
		}
	}

	private static void AddBundleNeeds(Item item, JsonArray sections)
	{
		Dictionary<string, string> bundleData = Game1.netWorldState?.Value?.BundleData;
		if (bundleData == null || Game1.getLocationFromName("CommunityCenter") is not CommunityCenter center)
		{
			return;
		}
		Dictionary<int, bool[]> done = center.bundlesDict();
		var needed = new List<string>();
		foreach (var (key, value) in bundleData)
		{
			string[] fields = value.Split('/');
			if (fields.Length < 3 || !int.TryParse(key.Split('/')[1], out int index))
			{
				continue;
			}
			string[] ingredients = ArgUtility.SplitBySpace(fields[2]);
			for (int i = 0; i + 2 < ingredients.Length; i += 3)
			{
				string id = ingredients[i];
				if (int.TryParse(id, out int n) && n < 0)
				{
					continue; // gold or a category requirement
				}
				string qualified = ItemRegistry.GetData(id)?.QualifiedItemId ?? "(O)" + id;
				if (qualified != item.QualifiedItemId)
				{
					continue;
				}
				bool complete = done.TryGetValue(index, out bool[] slots) && i / 3 < slots.Length && slots[i / 3];
				string quality = ingredients[i + 2] switch { "1" => " (silver+)", "2" => " (gold+)", "4" => " (iridium)", _ => "" };
				needed.Add($"{fields[0]} Bundle: {ingredients[i + 1]}×{quality}{(complete ? " ✓ done" : "")}");
			}
		}
		if (needed.Count > 0)
		{
			sections.Add(Section("Community Center", needed));
		}
	}

	internal static JsonObject NpcCard(NPC npc)
	{
		var facts = new JsonArray();
		var sections = new JsonArray();

		if (!string.IsNullOrEmpty(npc.Birthday_Season) && npc.Birthday_Day > 0)
		{
			facts.Add(Fact("Birthday", $"{Capitalize(npc.Birthday_Season)} {npc.Birthday_Day}"));
		}
		if (Game1.player.friendshipData.TryGetValue(npc.Name, out Friendship friendship))
		{
			facts.Add(Fact("Hearts", $"{friendship.Points / NPC.friendshipPointsPerHeartLevel} ({friendship.Points} pts)"));
			facts.Add(Fact("Gifts this week", $"{friendship.GiftsThisWeek}/2"));
			facts.Add(Fact("Talked today", friendship.TalkedToToday ? "Yes" : "No"));
		}

		if (Game1.NPCGiftTastes != null && Game1.NPCGiftTastes.TryGetValue(npc.Name, out string raw))
		{
			// Format: loveText/loveIds/likeText/likeIds/dislikeText/dislikeIds/hateText/hateIds/neutralText/neutralIds
			string[] f = raw.Split('/');
			AddGiftList(sections, "Loves", f, 1);
			AddGiftList(sections, "Likes", f, 3);
			AddGiftList(sections, "Dislikes", f, 5);
			AddGiftList(sections, "Hates", f, 7);
		}

		return new JsonObject
		{
			["kind"] = "npc",
			["title"] = npc.displayName,
			["subtitle"] = "Villager",
			["wiki"] = npc.Name,
			["facts"] = facts,
			["sections"] = sections,
		};
	}

	private static void AddGiftList(JsonArray sections, string title, string[] fields, int index)
	{
		if (index >= fields.Length)
		{
			return;
		}
		var names = new List<string>();
		foreach (string id in ArgUtility.SplitBySpace(fields[index]))
		{
			if (int.TryParse(id, out int n) && n < 0)
			{
				names.Add("All " + StardewValley.Object.GetCategoryDisplayName(n));
			}
			else if (ItemRegistry.GetData(id) is ParsedItemData data)
			{
				names.Add(data.DisplayName);
			}
		}
		if (names.Count > 0)
		{
			sections.Add(Section(title + " (personal)", names));
		}
	}

	// ---------- helpers ----------

	private static JsonArray Fact(string label, string value) => new JsonArray(label, value);

	private static JsonObject Section(string title, IEnumerable<string> lines) => new JsonObject
	{
		["title"] = title,
		["items"] = new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()),
	};

	private static string Seasons(CropData crop) =>
		crop.Seasons.Count == 0 ? "Any" : string.Join(", ", crop.Seasons.Select(s => s.ToString()));

	private static string FishTimes(string raw)
	{
		string[] t = ArgUtility.SplitBySpace(raw);
		var ranges = new List<string>();
		for (int i = 0; i + 1 < t.Length; i += 2)
		{
			if (int.TryParse(t[i], out int from) && int.TryParse(t[i + 1], out int to))
			{
				ranges.Add(from <= 600 && to >= 2600 ? "Any time" : $"{Clock(from)}–{Clock(to)}");
			}
		}
		return ranges.Count == 0 ? "Any time" : string.Join(", ", ranges);
	}

	private static string Clock(int time)
	{
		int hour = time / 100 % 24;
		return $"{(hour % 12 == 0 ? 12 : hour % 12)}{(hour < 12 ? "am" : "pm")}";
	}

	private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

	private static string SafeDescription(Item item)
	{
		try
		{
			return item.getDescription()?.Replace('^', '\n');
		}
		catch
		{
			return null;
		}
	}
}
