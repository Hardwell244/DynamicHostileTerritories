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
    /// LemonUI interaction menu. Shows the live status of the territory the player is
    /// in and exposes two actions: force-pacify the current area and toggle map blips.
    /// Owns the blips it creates and cleans them up on <see cref="Dispose"/>.
    /// </summary>
    public sealed class InteractionMenu
    {
        private readonly PluginSettings _settings;
        private readonly TerritoryController _controller;
        private readonly TerritoryRepository _repository;

        private readonly ObjectPool _pool = new ObjectPool();
        private readonly List<Blip> _blips = new List<Blip>();

        private NativeMenu _menu;
        private NativeItem _statusItem;
        private NativeItem _pacifyItem;
        private NativeCheckboxItem _blipsItem;

        public InteractionMenu(PluginSettings settings, TerritoryController controller, TerritoryRepository repository)
        {
            _settings = settings;
            _controller = controller;
            _repository = repository;
            BuildMenu();
        }

        private void BuildMenu()
        {
            _menu = new NativeMenu("Hostile Territories", "Field Control");
            _pool.Add(_menu);

            _statusItem = new NativeItem("Current Area", "The territory you are currently inside.")
            {
                Enabled = false
            };
            _menu.Add(_statusItem);

            _pacifyItem = new NativeItem("Force Pacify Current Area", "Break the controlling gang's hold here right now.");
            _pacifyItem.Activated += (sender, args) => _controller.ForcePacifyActive();
            _menu.Add(_pacifyItem);

            _blipsItem = new NativeCheckboxItem("Show Territory Blips", false);
            _blipsItem.CheckboxChanged += (sender, args) => SetBlips(_blipsItem.Checked);
            _menu.Add(_blipsItem);
        }

        /// <summary>
        /// Called every frame from the input fiber: handles the toggle key, refreshes
        /// the status line and lets LemonUI draw/process the menu.
        /// </summary>
        public void Process()
        {
            if (Game.IsKeyDown(_settings.MenuKey))
                _menu.Visible = !_menu.Visible;

            if (_menu.Visible)
                RefreshStatus();

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
                        Color = t.ControllingGang.BlipColor,
                        Alpha = 0.45f,
                        Name = t.Name + " (" + t.ControllingGang.Name + ")"
                    };
                    _blips.Add(blip);
                }
            }
            else
            {
                DeleteBlips();
            }
        }

        private void DeleteBlips()
        {
            foreach (Blip blip in _blips)
            {
                if (blip.Exists())
                    blip.Delete();
            }
            _blips.Clear();
        }

        /// <summary>
        /// Hides the menu and removes every blip this menu created.
        /// </summary>
        public void Dispose()
        {
            if (_menu != null)
                _menu.Visible = false;

            DeleteBlips();
        }
    }
}