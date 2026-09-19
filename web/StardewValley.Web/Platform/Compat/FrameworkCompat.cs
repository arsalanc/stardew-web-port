// Web-only stand-ins for members that exist in Stardew's customised MonoGame but not in KNI.
// C# 14 extension members let the shared game code compile unchanged.
// Each stub notes what the desktop behaviour is so we can revisit anything that matters visually.

namespace Microsoft.Xna.Framework.Graphics
{
	public static class TextureCompat
	{
		extension(Texture2D texture)
		{
			// Desktop MonoGame tracks a texture's allocated size separately from its logical
			// "image size". KNI textures are always exactly their allocated size.
			public int ActualWidth => texture.Width;

			public int ActualHeight => texture.Height;

			// Desktop: changes the logical size reported for the texture. No-op here.
			public void SetImageSize(int width, int height)
			{
			}
		}
	}

	public static class SpriteBatchCompat
	{
		private static float textureTuckAmount;

		extension(SpriteBatch)
		{
			// Desktop: insets source rectangles by this many texels to stop neighbouring sprites
			// bleeding in. Stored but not applied yet; if we see seams, the web SpriteBatch needs it.
			public static float TextureTuckAmount
			{
				get => textureTuckAmount;
				set => textureTuckAmount = value;
			}
		}
	}
}

namespace Microsoft.Xna.Framework
{
	public static class GameWindowCompat
	{
		extension(GameWindow window)
		{
			// A browser tab has exactly one "display": the canvas.
			public int GetDisplayIndex() => 0;

			public Rectangle GetDisplayBounds(int displayIndex) => new Rectangle(0, 0, window.ClientBounds.Width, window.ClientBounds.Height);

			public bool CenterOnDisplay(int displayIndex) => true;
		}
	}
}
