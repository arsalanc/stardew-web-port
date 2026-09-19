// Port helper (not part of the original game).
// Display-mode queries go through here so the browser build can answer them:
// KNI's WebGL adapter throws NotImplementedException for SupportedDisplayModes.
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace StardewValley;

internal static class PortDisplayModes
{
#if WEB
	private static readonly System.Reflection.ConstructorInfo DisplayModeCtor = typeof(DisplayMode).GetConstructor(
		System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
		null, new[] { typeof(int), typeof(int), typeof(SurfaceFormat) }, null);

	private static DisplayMode cached;

	/// <summary>The browser has one "display mode": the canvas's current size.</summary>
	private static DisplayMode CanvasMode(GraphicsDevice device)
	{
		PresentationParameters pp = device.PresentationParameters;
		if (cached == null || cached.Width != pp.BackBufferWidth || cached.Height != pp.BackBufferHeight)
		{
			cached = (DisplayMode)DisplayModeCtor.Invoke(new object[] { pp.BackBufferWidth, pp.BackBufferHeight, SurfaceFormat.Color });
		}
		return cached;
	}

	public static IEnumerable<DisplayMode> Supported(GraphicsDevice device) => new[] { CanvasMode(device) };

	public static DisplayMode Current(GraphicsDevice device) => CanvasMode(device);
#else
	public static IEnumerable<DisplayMode> Supported(GraphicsDevice device) => device.Adapter.SupportedDisplayModes;

	public static DisplayMode Current(GraphicsDevice device) => device.Adapter.CurrentDisplayMode;
#endif
}
