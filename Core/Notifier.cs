using System.Text;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Core
{
    /// <summary>
    /// Central helper for rich, textured LSPD-style notifications, so the mod's feedback
    /// looks like a proper police mod instead of plain text. It streams a character
    /// texture dictionary, shows the textured notification, and falls back to a plain
    /// notification if the texture can't load. It never throws.
    ///
    /// All text is sanitised to plain ASCII before display: the game's notification font
    /// does NOT render characters like the em-dash (—), en-dash (–) or curly quotes, and
    /// shows them as a broken □ box. Sanitising centrally here means callers can write
    /// natural text and never produce that box on screen.
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

        /// <summary>
        /// Replaces characters the game's notification font can't render (which show up as a
        /// □ box) with safe ASCII equivalents, and strips anything else non-ASCII. Game text
        /// formatting codes like ~r~ / ~y~ are plain ASCII and pass through untouched.
        /// </summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\u2014': // — em-dash
                    case '\u2013': // – en-dash
                        sb.Append('-');
                        break;
                    case '\u2018': // ‘
                    case '\u2019': // ’
                        sb.Append('\'');
                        break;
                    case '\u201C': // “
                    case '\u201D': // ”
                        sb.Append('"');
                        break;
                    case '\u2026': // …
                        sb.Append("...");
                        break;
                    default:
                        // Keep printable ASCII; drop anything else that would render as a box.
                        if (c >= 0x20 && c < 0x7F)
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Rich notification with the default police icon.</summary>
        public static void Show(string title, string subtitle, string text)
        {
            Show(PoliceIcon, title, subtitle, text);
        }

        /// <summary>Rich notification with a specific texture dictionary / icon.</summary>
        public static void Show(string icon, string title, string subtitle, string text)
        {
            title = Sanitize(title);
            subtitle = Sanitize(subtitle);
            text = Sanitize(text);

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