using System.Collections.Generic;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Data;
using DynamicHostileTerritories.Services;
using LemonUI;
using LemonUI.Menus;
using Rage;

namespace DynamicHostileTerritories.UI
{
    /// <summary>
    /// LemonUI interaction menu. Shows the live status of the territory the player is in,
    /// a city-wide gang-control overview, a gang-intelligence board (who to hit first, plus
    /// allies/rivals/wars), force-pacify, and a live map layer that paints every turf by its
    /// controlling gang and grip. Owns its blips; cleans them on Dispose.
    /// </summary>
    public sealed class InteractionMenu
    {
        private sealed class GangControl
        {
            public int Total;
            public int Pacified;
            public float GripSum;
        }

        private readonly PluginSettings _settings;
        private readonly TerritoryController _controller;
        private readonly TerritoryRepository _repository;
        private readonly GangWarfareDirector _warfare;

        private readonly ObjectPool _pool = new ObjectPool();
        private readonly Dictionary<Territory, Blip> _blips = new Dictionary<Territory, Blip>();

        private NativeMenu _menu;
        private NativeMenu _controlMenu;
        private NativeMenu _intelMenu;
        private NativeItem _statusItem;
        private NativeItem _pacifyItem;
        private NativeItem _controlItem;
        private NativeItem _intelItem;
        private NativeCheckboxItem _blipsItem;

        public InteractionMenu(PluginSettings settings, TerritoryController controller, TerritoryRepository repository, GangWarfareDirector warfare)
        {
            _settings = settings;
            _controller = controller;
            _repository = repository;
            _warfare = warfare;
            BuildMenu();
        }

        private void BuildMenu()
        {
            _menu = new NativeMenu("Hostile Territories", "Field Control");
            _pool.Add(_menu);

            _controlMenu = new NativeMenu("Hostile Territories", "City Control");
            _pool.Add(_controlMenu);

            _intelMenu = new NativeMenu("Hostile Territories", "Gang Intelligence");
            _pool.Add(_intelMenu);

            _statusItem = new NativeItem("Current Area", "The territory you are currently inside.")
            {
                Enabled = false
            };
            _menu.Add(_statusItem);

            _pacifyItem = new NativeItem("Force Pacify Current Area", "Break the controlling gang's hold here right now.");
            _pacifyItem.Activated += (sender, args) => _controller.ForcePacifyActive();
            _menu.Add(_pacifyItem);

            _controlItem = new NativeItem("City Control Overview", "See which gangs still hold the city and how far you've pushed them back.");
            _controlItem.Activated += (sender, args) =>
            {
                PopulateControlMenu();
                _menu.Visible = false;
                _controlMenu.Visible = true;
            };
            _menu.Add(_controlItem);

            _intelItem = new NativeItem("Gang Intelligence", "Rank gangs by power, see who to hit first, plus their allies, rivals and current wars.");
            _intelItem.Activated += (sender, args) =>
            {
                PopulateIntelMenu();
                _menu.Visible = false;
                _intelMenu.Visible = true;
            };
            _menu.Add(_intelItem);

            _blipsItem = new NativeCheckboxItem(
                "Show Territory Map",
                "Paint every gang turf on the map. Bold = strong grip, faint = pacified.",
                false
            );

            // Wire the checkbox to the map blip layer (create / delete blips).
            _blipsItem.CheckboxChanged += (sender, args) => SetBlips(_blipsItem.Checked);

            // Add the item to the visible menu.
            _menu.Add(_blipsItem);
        }

        /// <summary>
        /// Called every frame from the input fiber: handles the toggle key, refreshes the
        /// status line, keeps the live map layer up to date, and lets LemonUI draw.
        /// </summary>
        public void Process()
        {
            if (Game.IsKeyDown(_settings.MenuKey))
                _menu.Visible = !_menu.Visible;

            if (_menu.Visible)
                RefreshStatus();

            if (_blipsItem.Checked && _blips.Count > 0)
                UpdateBlips();

            _pool.Process();
        }

        private void RefreshStatus()
        {
            Territory active = _controller.ActiveTerritory;

            if (active == null)
            {
                _statusItem.AltTitle = "Clear";
                _statusItem.Description = "You are not inside any hostile territory.";
                _pacifyItem.Enabled = false;
                return;
            }

            _statusItem.AltTitle = active.ControllingGang != null ? active.ControllingGang.Name : "Unknown";
            _statusItem.Description = active.Name + " - threat " + active.Hostility
                + ", gang grip " + (int)active.Strength + "%.";
            _pacifyItem.Enabled = true;
        }

        // --- City control overview --------------------------------------------------------

        private void PopulateControlMenu()
        {
            _controlMenu.Clear();

            Dictionary<Gang, GangControl> byGang = new Dictionary<Gang, GangControl>();
            int totalTurfs = 0;
            int totalPacified = 0;

            foreach (Territory t in _repository.Territories)
            {
                if (t.ControllingGang == null)
                    continue; // a null owner can never be a dictionary key - skip it

                totalTurfs++;
                bool pacified = IsPacified(t);
                if (pacified) totalPacified++;

                if (!byGang.TryGetValue(t.ControllingGang, out GangControl gc))
                {
                    gc = new GangControl();
                    byGang[t.ControllingGang] = gc;
                }

                gc.Total++;
                gc.GripSum += t.Strength;
                if (pacified) gc.Pacified++;
            }

            int reclaimedPct = totalTurfs > 0 ? (totalPacified * 100) / totalTurfs : 0;

            Gang kingpin = _warfare != null ? _warfare.Kingpin : null;

            NativeItem header = new NativeItem("City Reclaimed",
                "Turfs you've pacified across Los Santos."
                + (kingpin != null ? " Most-wanted gang: " + kingpin.Name + "." : ""))
            {
                AltTitle = reclaimedPct + "%",
                Enabled = false
            };
            _controlMenu.Add(header);

            foreach (KeyValuePair<Gang, GangControl> kv in byGang)
            {
                GangControl gc = kv.Value;
                int held = gc.Total - gc.Pacified;
                int avgGrip = gc.Total > 0 ? (int)(gc.GripSum / gc.Total) : 0;

                GangWarfareDirector.PowerInfo info = _warfare != null
                    ? _warfare.GetPower(kv.Key)
                    : new GangWarfareDirector.PowerInfo();

                NativeItem item = new NativeItem(kv.Key.Name,
                    "Average grip " + avgGrip + "%. " + gc.Pacified + " of " + gc.Total + " turfs pacified."
                    + " War chest - money " + (int)info.Money + ", influence " + (int)info.Influence
                    + ", weapons " + (int)info.Weapons + ".")
                {
                    // Keep the AltTitle SHORT so a long gang name never collides with it.
                    AltTitle = held + "/" + gc.Total + " held  |  PWR " + (int)info.Power,
                    Enabled = false
                };
                _controlMenu.Add(item);
            }
        }

        // --- Gang intelligence board ------------------------------------------------------

        private void PopulateIntelMenu()
        {
            _intelMenu.Clear();

            Gang kingpin = _warfare != null ? _warfare.Kingpin : null;
            GangDiplomacy dip = _warfare != null ? _warfare.Diplomacy : null;

            // Count holdings per gang.
            Dictionary<Gang, int> held = new Dictionary<Gang, int>();
            foreach (Territory t in _repository.Territories)
            {
                if (t.ControllingGang == null)
                    continue;
                int c;
                held.TryGetValue(t.ControllingGang, out c);
                held[t.ControllingGang] = c + 1;
            }

            // Rank by power, strongest first - that's the order to hit them in.
            List<Gang> gangs = new List<Gang>(held.Keys);
            gangs.Sort((a, b) => Power(b).CompareTo(Power(a)));

            NativeItem header = new NativeItem("Intelligence Board",
                kingpin != null
                    ? "Most-wanted gang: " + kingpin.Name + ". Hit their people to drain their war chest and stall their expansion."
                    : "No dominant gang right now.")
            {
                AltTitle = gangs.Count + " gangs",
                Enabled = false
            };
            _intelMenu.Add(header);

            int total = gangs.Count;
            for (int i = 0; i < total; i++)
            {
                Gang g = gangs[i];
                GangWarfareDirector.PowerInfo info = _warfare != null
                    ? _warfare.GetPower(g)
                    : new GangWarfareDirector.PowerInfo();
                int turfs = held[g];

                string badge;
                string threat;
                if (kingpin != null && g == kingpin)
                {
                    badge = "KINGPIN";
                    threat = "KINGPIN - the dominant gang, top priority.";
                }
                else if (i <= total / 3)
                {
                    badge = "HIGH";
                    threat = "HIGH THREAT.";
                }
                else if (i <= (2 * total) / 3)
                {
                    badge = "ACTIVE";
                    threat = "ACTIVE.";
                }
                else
                {
                    badge = "FADING";
                    threat = "FADING.";
                }

                string allies = dip != null ? Names(dip.Allies(g)) : "";
                string enemies = dip != null ? Names(dip.Enemies(g)) : "";
                Gang warTarget = _warfare != null ? _warfare.CurrentWarTarget(g) : null;

                string desc = threat
                    + " PWR " + (int)info.Power + ", " + turfs + (turfs == 1 ? " turf" : " turfs") + " held."
                    + " War chest - money " + (int)info.Money + ", weapons " + (int)info.Weapons + "."
                    + " Allies: " + (allies.Length > 0 ? allies : "none") + "."
                    + " Rivals: " + (enemies.Length > 0 ? enemies : "none") + "."
                    + (warTarget != null ? " At war with " + warTarget.Name + "." : "");

                NativeItem item = new NativeItem(g.Name, desc)
                {
                    AltTitle = badge, // kept short on purpose so it never collides with the name
                    Enabled = false
                };
                _intelMenu.Add(item);
            }
        }

        private double Power(Gang gang)
        {
            return _warfare != null ? _warfare.GetPower(gang).Power : 0.0;
        }

        private static string Names(IEnumerable<Gang> gangs)
        {
            List<string> names = new List<string>();
            foreach (Gang g in gangs)
                if (g != null)
                    names.Add(g.Name);
            return string.Join(", ", names);
        }

        // --- Live map layer ---------------------------------------------------------------

        private void SetBlips(bool show)
        {
            if (show)
            {
                if (_blips.Count > 0)
                    return;

                foreach (Territory t in _repository.Territories)
                {
                    if (t.ControllingGang == null)
                        continue;

                    try
                    {
                        Blip blip = new Blip(t.Center, t.Radius)
                        {
                            Color = t.ControllingGang.BlipColor
                        };
                        _blips[t] = blip;
                    }
                    catch { /* a blip failure must never break the menu */ }
                }

                UpdateBlips();
            }
            else
            {
                DeleteBlips();
            }
        }

        private void UpdateBlips()
        {
            foreach (KeyValuePair<Territory, Blip> kv in _blips)
            {
                Territory t = kv.Key;
                Blip blip = kv.Value;
                if (!blip.Exists())
                    continue;

                // Recolour to the current owner so a conquest repaints the turf live.
                if (t.ControllingGang != null)
                    blip.Color = t.ControllingGang.BlipColor;

                // Bold when the gang's grip is strong, faint once you've pacified it.
                blip.Alpha = 0.15f + (t.Strength / 100f) * 0.5f;

                string owner = t.ControllingGang != null ? t.ControllingGang.Name : "Unknown";
                string state = IsPacified(t) ? "PACIFIED" : (int)t.Strength + "% grip";
                blip.Name = t.Name + " (" + owner + ") - " + state;
            }
        }

        private void DeleteBlips()
        {
            foreach (KeyValuePair<Territory, Blip> kv in _blips)
            {
                if (kv.Value.Exists())
                    kv.Value.Delete();
            }
            _blips.Clear();
        }

        private bool IsPacified(Territory t)
        {
            return t.Strength <= _settings.PacifiedBelow;
        }

        /// <summary>
        /// Hides the menus and removes every blip this menu created.
        /// </summary>
        public void Dispose()
        {
            if (_menu != null)
                _menu.Visible = false;
            if (_controlMenu != null)
                _controlMenu.Visible = false;
            if (_intelMenu != null)
                _intelMenu.Visible = false;

            DeleteBlips();
        }
    }
}