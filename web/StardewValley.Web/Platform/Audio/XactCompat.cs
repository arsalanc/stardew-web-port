// Web-only stand-ins for audio types that exist in Stardew's customised MonoGame but not in KNI.
// They let the shared game code compile unchanged. The web build currently uses DummySoundBank,
// so these only carry data; the Web Audio backend will give them real behaviour.
using System;

namespace Microsoft.Xna.Framework.Audio
{
	/// <summary>A runtime-defined sound cue (used by Data/AudioChanges).</summary>
	public class CueDefinition
	{
		public string name;

		public SoundEffect[] sounds = Array.Empty<SoundEffect>();

		public int instanceLimit = 1;

		public bool looped;

		public bool useReverb;

		public int categoryIndex;

		/// <summary>Raised after the definition's sounds are changed.</summary>
		public Action OnModified;

		public void SetSound(SoundEffect[] sounds, int categoryIndex, bool loop = false, bool useReverb = false)
		{
			this.sounds = sounds ?? Array.Empty<SoundEffect>();
			this.categoryIndex = categoryIndex;
			looped = loop;
			this.useReverb = useReverb;
		}
	}
}
