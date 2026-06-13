using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Core
{
    /// <summary>
    /// Central helper for rich, textured LSPD-style notifications, so the mod's feedback
    /// looks like a proper police mod instead of plain text. It streams a character
    /// texture dictionary, shows the textured notification, and falls back to a plain
    /// notification if the texture can't load. It never throws.
    /// </summary>
    public static class Notifier
    {
        // 911 / police dispatch icon — fits a law-enforcement mod.
        public const string PoliceIcon = "CHAR_CALL911";

        private static void EnsureDict(string dict)
        {
            if (NativeFunction.Natives.HAS_STREAMED_TEXTURE_DICT_LOADED<bool>(dict))
                return;

            NativeFunction.Natives.REQUEST_STREAMED_TEXTURE_DICT(dict, true);

            int tries = 0;
            while (!NativeFunction.Natives.HAS_STREAMED_TEXTURE_DICT_LOADED<bool>(dict) && tries < 50)
            {
                GameFiber.Sleep(5);
                tries++;
            }
        }

        /// <summary>Rich notification with the default police icon.</summary>
        public static void Show(string title, string subtitle, string text)
        {
            Show(PoliceIcon, title, subtitle, text);
        }

        /// <summary>Rich notification with a specific texture dictionary / icon.</summary>
        public static void Show(string icon, string title, string subtitle, string text)
        {
            try
            {
                EnsureDict(icon);
                Game.DisplayNotification(icon, icon, title, subtitle, text);
            }
            catch
            {
                // If anything about the textured path fails, the player still gets the message.
                try { Game.DisplayNotification(text); } catch { }
            }
        }
    }
}