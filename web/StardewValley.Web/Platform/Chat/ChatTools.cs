using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.WebPlatform.Wiki;

namespace StardewValley.WebPlatform.Chat;

/// <summary>
/// Tools the in-game assistant (wwwroot/js/chatPanel.js) can call. They read the live game and
/// its data files, so answers reflect this game version and the player's own save rather than
/// the model's memory. Called from JS between frames, so the game is never mid-update.
/// </summary>
[SupportedOSPlatform("browser")]
public static class ChatTools
{
	/// <summary>Tool schemas in the neutral (Ollama/OpenAI function) format.</summary>
	public static string Definitions()
	{
		var tools = new JsonArray
		{
			Tool("get_player_state", "The player's current situation: date, time, weather today and tomorrow, money, energy, health, location, skill levels, luck, and inventory.", new JsonObject()),
			Tool("get_recipe", "How to craft or cook something: ingredients (and how many the player has), whether the player knows the recipe, how it's unlocked, and whether they can make it now.",
				new JsonObject { ["name"] = Param("Recipe or item name, e.g. \"Chest\" or \"Fried Egg\"") }, "name"),
			Tool("lookup_item", "Facts about an item: category, sell price, crop or fish details, which villagers love/like/dislike it as a gift, and Community Center bundles that still need it.",
				new JsonObject { ["name"] = Param("Item name, e.g. \"Sardine\"") }, "name"),
			Tool("lookup_npc", "A villager's birthday, the player's friendship with them (hearts, gifts this week, talked today) and their personal gift preferences.",
				new JsonObject { ["name"] = Param("Villager name, e.g. \"Shane\"") }, "name"),
			Tool("get_bundles_remaining", "Community Center bundles that aren't finished yet, with the items each one still needs.", new JsonObject()),
		};
		return tools.ToJsonString();
	}

	/// <summary>Runs a tool and returns a JSON result (an {"error": ...} object on failure).</summary>
	public static string Execute(string name, string argumentsJson)
	{
		try
		{
			if (Game1.player == null || !Game1.hasLoadedGame)
			{
				return Error("No save is loaded yet; the player is on the title screen.");
			}
			JsonObject args = string.IsNullOrWhiteSpace(argumentsJson) ? new JsonObject() : JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
			string text(string key) => args[key]?.GetValue<string>()?.Trim() ?? "";
			JsonNode result = name switch
			{
				"get_player_state" => PlayerState(),
				"get_recipe" => Recipe(text("name")),
				"lookup_item" => LookupItem(text("name")),
				"lookup_npc" => LookupNpc(text("name")),
				"get_bundles_remaining" => BundlesRemaining(),
				_ => ErrorNode($"Unknown tool '{name}'."),
			};
			return result.ToJsonString();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[port] Chat tool {name} failed: {ex}");
			return Error($"The tool failed: {ex.Message}");
		}
	}

	// ---------- tools ----------

	private static JsonObject PlayerState()
	{
		Farmer p = Game1.player;
		var inventory = new JsonArray();
		foreach (Item item in p.Items)
		{
			if (item != null)
			{
				inventory.Add(item.Stack > 1 ? $"{item.DisplayName} x{item.Stack}" : item.DisplayName);
			}
		}
		return new JsonObject
		{
			["date"] = $"{Game1.season} {Game1.dayOfMonth}, Year {Game1.year} ({Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)})",
			["time"] = Clock(Game1.timeOfDay),
			["weatherToday"] = Game1.isLightning ? "Storm" : Game1.isRaining ? "Rain" : Game1.isSnowing ? "Snow" : Game1.isDebrisWeather ? "Windy" : "Sunny",
			["weatherTomorrow"] = Game1.weatherForTomorrow,
			["money"] = $"{p.Money:N0}g",
			["energy"] = $"{(int)p.Stamina}/{p.MaxStamina}",
			["health"] = $"{p.health}/{p.maxHealth}",
			["location"] = Game1.currentLocation?.DisplayName,
			["skills"] = new JsonObject
			{
				["farming"] = p.FarmingLevel,
				["fishing"] = p.FishingLevel,
				["foraging"] = p.ForagingLevel,
				["mining"] = p.MiningLevel,
				["combat"] = p.CombatLevel,
			},
			["luckToday"] = p.DailyLuck switch
			{
				> 0.07 => "very good",
				> 0.02 => "good",
				>= -0.02 => "neutral",
				>= -0.07 => "bad",
				_ => "very bad",
			},
			["inventory"] = inventory,
		};
	}

	private static JsonObject Recipe(string query)
	{
		if (query.Length == 0)
		{
			return ErrorNode("Give a recipe name.");
		}
		string key = FindRecipe(query, out bool cooking);
		if (key == null)
		{
			var similar = AllRecipeNames().Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(n, StringComparison.OrdinalIgnoreCase)).Take(8);
			return ErrorNode($"No crafting or cooking recipe matches '{query}'.", similar);
		}

		var recipe = new CraftingRecipe(key, cooking);
		Farmer p = Game1.player;
		bool known = cooking ? p.cookingRecipes.ContainsKey(key) : p.craftingRecipes.ContainsKey(key);
		var ingredients = new JsonArray();
		bool haveAll = true;
		foreach (var (id, need) in recipe.recipeList)
		{
			int have = CountOwned(id);
			haveAll &= have >= need;
			ingredients.Add(new JsonObject { ["item"] = IngredientName(id), ["need"] = need, ["have"] = have });
		}

		string raw = (cooking ? CraftingRecipe.cookingRecipes : CraftingRecipe.craftingRecipes)[key];
		string[] fields = raw.Split('/');
		string unlock = fields.Length > (cooking ? 3 : 4) ? fields[cooking ? 3 : 4] : "";
		return new JsonObject
		{
			["recipe"] = recipe.DisplayName,
			["kind"] = cooking ? "Cooking (made in a kitchen)" : "Crafting (made from the Crafting tab of the menu)",
			["produces"] = recipe.numberProducedPerCraft,
			["ingredients"] = ingredients,
			["playerKnowsRecipe"] = known,
			["canMakeNow"] = known && haveAll,
			["howToUnlock"] = DescribeUnlock(unlock),
		};
	}

	private static JsonObject LookupItem(string query)
	{
		if (query.Length == 0)
		{
			return ErrorNode("Give an item name.");
		}
		string qualifiedId = FindItem(query);
		if (qualifiedId == null)
		{
			return ErrorNode($"No item matches '{query}'.");
		}
		JsonObject card = WikiContext.ItemCard(ItemRegistry.Create(qualifiedId));
		card.Remove("kind");
		card.Remove("wiki");
		return card;
	}

	private static JsonObject LookupNpc(string query)
	{
		NPC match = null;
		Utility.ForEachVillager(npc =>
		{
			if (npc.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || npc.displayName.Equals(query, StringComparison.OrdinalIgnoreCase))
			{
				match = npc;
				return false;
			}
			return true;
		});
		if (match == null)
		{
			return ErrorNode($"No villager named '{query}'.");
		}
		JsonObject card = WikiContext.NpcCard(match);
		card.Remove("kind");
		card.Remove("wiki");
		return card;
	}

	private static JsonObject BundlesRemaining()
	{
		Dictionary<string, string> bundleData = Game1.netWorldState?.Value?.BundleData;
		if (bundleData == null || Game1.getLocationFromName("CommunityCenter") is not CommunityCenter center)
		{
			return ErrorNode("Community Center bundle data isn't available.");
		}
		Dictionary<int, bool[]> done = center.bundlesDict();
		var rooms = new JsonObject();
		foreach (var (key, value) in bundleData)
		{
			string[] keyParts = key.Split('/');
			string[] fields = value.Split('/');
			if (fields.Length < 3 || keyParts.Length < 2 || !int.TryParse(keyParts[1], out int index))
			{
				continue;
			}
			bool[] slots = done.TryGetValue(index, out bool[] s) ? s : Array.Empty<bool>();
			string[] ingredients = ArgUtility.SplitBySpace(fields[2]);
			int total = ingredients.Length / 3;
			int required = fields.Length > 4 && int.TryParse(fields[4], out int n) && n > 0 ? n : total;
			int completed = slots.Take(total).Count(x => x);
			if (completed >= required)
			{
				continue;
			}
			var missing = new JsonArray();
			for (int i = 0; i + 2 < ingredients.Length; i += 3)
			{
				if (i / 3 < slots.Length && slots[i / 3])
				{
					continue;
				}
				string quality = ingredients[i + 2] switch { "1" => " (silver+)", "2" => " (gold+)", "4" => " (iridium)", _ => "" };
				string item = int.TryParse(ingredients[i], out int id) && id == -1 ? $"{ingredients[i + 1]}g" : $"{IngredientName(ingredients[i])} x{ingredients[i + 1]}{quality}";
				missing.Add(item);
			}
			if (rooms[keyParts[0]] is not JsonArray room)
			{
				rooms[keyParts[0]] = room = new JsonArray();
			}
			room.Add(new JsonObject
			{
				["bundle"] = fields[0],
				["progress"] = $"{completed}/{required} items",
				["stillNeeds"] = missing,
			});
		}
		return rooms.Count == 0 ? new JsonObject { ["message"] = "Every Community Center bundle is complete." } : rooms;
	}

	// ---------- lookups ----------

	private static IEnumerable<string> AllRecipeNames() => CraftingRecipe.craftingRecipes.Keys.Concat(CraftingRecipe.cookingRecipes.Keys);

	private static string FindRecipe(string query, out bool cooking)
	{
		string wanted = Normalize(query);
		foreach (bool isCooking in new[] { false, true })
		{
			foreach (string key in (isCooking ? CraftingRecipe.cookingRecipes : CraftingRecipe.craftingRecipes).Keys)
			{
				if (Normalize(key) == wanted || Normalize(new CraftingRecipe(key, isCooking).DisplayName) == wanted)
				{
					cooking = isCooking;
					return key;
				}
			}
		}
		cooking = false;
		return null;
	}

	private static Dictionary<string, string> itemIndex;

	/// <summary>Name -> qualified item ID, built once from every item type (objects win name clashes).</summary>
	private static string FindItem(string query)
	{
		if (itemIndex == null)
		{
			itemIndex = new Dictionary<string, string>();
			foreach (IItemDataDefinition type in ItemRegistry.ItemTypes)
			{
				foreach (string id in type.GetAllIds())
				{
					ParsedItemData data = ItemRegistry.GetData(type.Identifier + id);
					if (data == null)
					{
						continue;
					}
					itemIndex.TryAdd(Normalize(data.DisplayName), data.QualifiedItemId);
					itemIndex.TryAdd(Normalize(data.InternalName), data.QualifiedItemId);
				}
			}
		}
		string wanted = Normalize(query);
		if (itemIndex.TryGetValue(wanted, out string exact))
		{
			return exact;
		}
		// Tolerate plurals and small variations ("sardines", "a chest").
		string trimmed = wanted.TrimEnd('s');
		return itemIndex.TryGetValue(trimmed, out string plural) ? plural
			: itemIndex.FirstOrDefault(kv => kv.Key.Contains(wanted)).Value;
	}

	private static int CountOwned(string id)
	{
		if (int.TryParse(id, out int n) && n < 0)
		{
			return Game1.player.Items.Where(i => i != null && i.Category == n).Sum(i => i.Stack);
		}
		string qualified = ItemRegistry.QualifyItemId(id) ?? id;
		return Game1.player.Items.Where(i => i != null && i.QualifiedItemId == qualified).Sum(i => i.Stack);
	}

	private static string IngredientName(string id) =>
		int.TryParse(id, out int n) && n < 0
			? "Any " + StardewValley.Object.GetCategoryDisplayName(n)
			: ItemRegistry.GetData(id)?.DisplayName ?? id;

	private static string DescribeUnlock(string raw)
	{
		string[] p = ArgUtility.SplitBySpace(raw);
		if (p.Length == 0 || raw == "default")
		{
			return "Known from the start of the game.";
		}
		switch (p[0])
		{
		case "s" when p.Length >= 3:
			return $"Learned at {p[1]} level {p[2]}.";
		case "f" when p.Length >= 3:
			return $"Sent by {p[1]} at {p[2]} hearts of friendship.";
		case "l" when p.Length >= 2:
			return $"Unlocked at overall player level {p[1]}.";
		case "none":
		case "null":
			return "Not learned automatically; it comes from a shop, TV show, event or mail.";
		default:
			return $"Unlock condition from the game data: \"{raw}\".";
		}
	}

	// ---------- helpers ----------

	private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new JsonObject
	{
		["type"] = "function",
		["function"] = new JsonObject
		{
			["name"] = name,
			["description"] = description,
			["parameters"] = new JsonObject
			{
				["type"] = "object",
				["properties"] = properties,
				["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
			},
		},
	};

	private static JsonObject Param(string description) => new JsonObject { ["type"] = "string", ["description"] = description };

	private static string Normalize(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

	private static string Clock(int time)
	{
		int hour = time / 100 % 24;
		return $"{(hour % 12 == 0 ? 12 : hour % 12)}:{time % 100:00}{(hour < 12 ? "am" : "pm")}";
	}

	private static JsonObject ErrorNode(string message, IEnumerable<string> suggestions = null)
	{
		var o = new JsonObject { ["error"] = message };
		List<string> list = suggestions?.ToList();
		if (list is { Count: > 0 })
		{
			o["didYouMean"] = new JsonArray(list.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
		}
		return o;
	}

	private static string Error(string message) => ErrorNode(message).ToJsonString();
}
