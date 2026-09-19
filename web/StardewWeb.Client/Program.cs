using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace StardewWeb.Client
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");
            builder.Services.AddScoped(sp => new HttpClient()
            {
                BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
            });

            // Restore saves/options from IndexedDB into the in-memory file system before the game reads them.
            await StardewValley.WebPlatform.WebStorage.InitializeAsync("/js/stardewStorage.js");

            // Wiki side panel (also takes over window.innerWidth so the game resizes around it).
            await StardewValley.WebPlatform.Wiki.WikiContext.InitializeAsync("/js/wikiPanel.js");

            // Load the Web Audio backend and cue manifest before the game boots (it falls back to silence on failure).
            await StardewValley.WebPlatform.Audio.WebAudio.InitializeAsync("/js/stardewAudio.js", "/Audio/audio.json");

            await builder.Build().RunAsync();
        }
    }
}
