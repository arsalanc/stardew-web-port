// Make the compat extension members visible everywhere without importing whole namespaces
// (a global "using Microsoft.Xna.Framework;" would clash with xTile.Dimensions.Rectangle etc.).
global using static Microsoft.Xna.Framework.GameWindowCompat;
global using static Microsoft.Xna.Framework.Graphics.TextureCompat;
global using static Microsoft.Xna.Framework.Graphics.SpriteBatchCompat;
