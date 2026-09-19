using Microsoft.JSInterop;
using StardewValley.WebPlatform;

namespace StardewWeb.Client.Pages
{
    public partial class Index
    {
        private string _fault;

        protected override void OnAfterRender(bool firstRender)
        {
            base.OnAfterRender(firstRender);

            if (firstRender)
            {
                JsRuntime.InvokeAsync<object>("initRenderJS", DotNetObjectReference.Create(this));
            }
        }

        /// <summary>Tool schemas for the in-game assistant (wwwroot/js/chatPanel.js).</summary>
        [JSInvokable]
        public string ChatToolDefinitions() => StardewValley.WebPlatform.Chat.ChatTools.Definitions();

        /// <summary>Runs an assistant tool against the live game; returns JSON.</summary>
        [JSInvokable]
        public string ChatTool(string name, string argumentsJson) => StardewValley.WebPlatform.Chat.ChatTools.Execute(name, argumentsJson);

        /// <summary>Frame timing line for the ?perf overlay.</summary>
        [JSInvokable]
        public string PerfSummary() => WebEntry.PerfSummary();

        /// <summary>Called once per frame from index.html. Returns whether the OS cursor should show.</summary>
        [JSInvokable]
        public bool TickDotNet()
        {
            if (_fault != null)
            {
                return true;
            }
            WebEntry.Tick();
            if (WebEntry.Fault != null)
            {
                _fault = "Stardew Valley crashed in the browser build:\n\n" + WebEntry.Fault;
                StateHasChanged();
                return true;
            }
            return WebEntry.IsMouseVisible;
        }
    }
}
