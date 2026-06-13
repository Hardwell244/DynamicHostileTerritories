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
    /// a city-wide gang-control overview, and exposes force-pacify plus a live map layer
    /// that paints every turf by its controlling gang and grip — so the player can watch
    /// the city being reclaimed as they pacify areas. Owns its blips; cleans them on Dispose.
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
        private NativeItem _statusItem;
        private NativeItem _pacifyItem;
        private NativeItem _controlItem;
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

            _blipsItem = new NativeCheckboxItem(
                "Show Territory Map",
                "Paint every gang turf on the map. Bold = strong grip, faint = pacified.",
                false
            );

            // 1. Conecta a caixa de seleção à função que cria/deleta os blips no mapa
            _blipsItem.CheckboxChanged += (sender, args) => SetBlips(_blipsItem.Checked);

            // 2. Adiciona o botão de fato ao menu visível
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

            _statusItem.AltTitle = active.ControllingGang.Name;
            _statusItem.Description = active.Name + " — threat " + active.Hostility
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

            NativeItem header = new NativeItem("City Reclaimed",
                "Turfs you've pacified across Los Santos.")
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
                    + " War chest — money " + (int)info.Money + ", influence " + (int)info.Influence
                    + ", weapons " + (int)info.Weapons + ".")
                {
                    AltTitle = held + "/" + gc.Total + " held  |  PWR " + (int)info.Power,
                    Enabled = false
                };
                _controlMenu.Add(item);
            }
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
                    Blip blip = new Blip(t.Center, t.Radius)
                    {
                        Color = t.ControllingGang.BlipColor
                    };
                    _blips[t] = blip;
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
                blip.Color = t.ControllingGang.BlipColor;

                // Bold when the gang's grip is strong, faint once you've pacified it.
                blip.Alpha = 0.15f + (t.Strength / 100f) * 0.5f;

                string state = IsPacified(t) ? "PACIFIED" : (int)t.Strength + "% grip";
                blip.Name = t.Name + " (" + t.ControllingGang.Name + ") — " + state;
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

            DeleteBlips();
        }
    }
}