using System;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Services;
using DynamicHostileTerritories.UI;
using LSPD_First_Response.Mod.API;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Core
{
    /// <summary>
    /// Plugin entry point. Loaded by LSPDFR (inherits the LSPDFR Plugin base class).
    /// Builds the service graph once, then starts and stops the live systems in step
    /// with the player's on-duty state. Everything it starts, it tears down again.
    /// </summary>
    public class EntryPoint : Plugin
    {
        private const string PoliceTextureDict = "WEB_LOSSANTOSPOLICEDEPT";

        private PluginSettings _settings;
        private TerritoryRepository _repository;
        private TerritoryStateStore _stateStore;
        private HostilityCalculator _hostility;
        private GangSpawnManager _spawnManager;
        private EncounterDirector _director;
        private TerritoryController _controller;
        private InteractionMenu _menu;

        private GameFiber _inputFiber;
        private bool _onDuty;

        public override void Initialize()
        {
            _settings = PluginSettings.Load();

            if (!_settings.Enabled)
            {
                Game.LogTrivial("[DHT] Plugin is disabled in the configuration. Not starting.");
                return;
            }

            // Build the service graph. Nothing here touches the game world yet.
            _repository = new TerritoryRepository();
            _stateStore = new TerritoryStateStore();
            _stateStore.Apply(_repository.Territories);

            _hostility = new HostilityCalculator();
            _spawnManager = new GangSpawnManager(_settings.MaxSpawnedPeds);
            _director = new EncounterDirector(_settings, _hostility, _spawnManager);
            _controller = new TerritoryController(_settings, _repository, _director, _spawnManager, _stateStore);
            _menu = new InteractionMenu(_settings, _controller, _repository);

            Functions.OnOnDutyStateChanged += OnOnDutyStateChanged;
            Game.LogTrivial("[DHT] Dynamic Hostile Territories loaded. Waiting for on-duty.");
        }

        public override void Finally()
        {
            Functions.OnOnDutyStateChanged -= OnOnDutyStateChanged;
            Shutdown();
            Game.LogTrivial("[DHT] Dynamic Hostile Territories unloaded.");
        }

        private void OnOnDutyStateChanged(bool onDuty)
        {
            if (onDuty)
                Startup();
            else
                Shutdown();
        }

        private void Startup()
        {
            if (_onDuty || _settings == null || !_settings.Enabled)
                return;

            _onDuty = true;
            _controller.Start();
            _inputFiber = GameFiber.StartNew(InputLoop);
            GameFiber.StartNew(ShowWelcomeNotification);

            Game.LogTrivial("[DHT] Player went on duty. Systems online.");
        }

        /// <summary>
        /// Idempotent teardown: safe to call on off-duty and again on unload.
        /// </summary>
        private void Shutdown()
        {
            _onDuty = false;

            if (_inputFiber != null && _inputFiber.IsAlive)
                _inputFiber.Abort();
            _inputFiber = null;

            _menu?.Dispose();
            _controller?.Stop();
        }

        private void InputLoop()
        {
            while (_onDuty)
            {
                try
                {
                    _menu.Process();
                }
                catch (Exception ex)
                {
                    Game.LogTrivial("[DHT] Menu loop error: " + ex);
                }

                GameFiber.Yield();
            }
        }

        private void ShowWelcomeNotification()
        {
            try
            {
                NativeFunction.Natives.REQUEST_STREAMED_TEXTURE_DICT(PoliceTextureDict, true);

                int attempts = 0;
                while (!NativeFunction.Natives.HAS_STREAMED_TEXTURE_DICT_LOADED<bool>(PoliceTextureDict) && attempts < 100)
                {
                    GameFiber.Sleep(10);
                    attempts++;
                }

                Game.DisplayNotification(
                    PoliceTextureDict,
                    PoliceTextureDict,
                    "Dynamic Hostile Territories",
                    "~b~Los Santos Police Dept.",
                    "Active. Stay alert when entering gang territories.");

                NativeFunction.Natives.SET_STREAMED_TEXTURE_DICT_AS_NO_LONGER_NEEDED(PoliceTextureDict);
            }
            catch (Exception ex)
            {
                Game.LogTrivial("[DHT] Failed to display welcome notification: " + ex);
            }
        }
    }
}