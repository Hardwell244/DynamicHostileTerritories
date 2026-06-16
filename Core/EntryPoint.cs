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
        private GangWarfareDirector _warfare;
        private RetaliationDirector _retaliation;
        private InteractionMenu _menu;

        private GameFiber _inputFiber;
        private int _inputGen;
        private bool _onDuty;

        public override void Initialize()
        {
            _settings = PluginSettings.Load();
            Logger.Initialize(_settings.DebugLogging);
            Logger.Info("Dynamic Hostile Territories initializing. DebugLogging=" + _settings.DebugLogging + ".");

            if (!_settings.Enabled)
            {
                Logger.Info("Plugin is disabled in the configuration. Not starting.");
                return;
            }

            try
            {
                _repository = new TerritoryRepository();
                _stateStore = new TerritoryStateStore();
                _stateStore.Apply(_repository.Territories);

                _hostility = new HostilityCalculator(_settings);
                _spawnManager = new GangSpawnManager(_settings.MaxSpawnedPeds);
                _director = new EncounterDirector(_settings, _hostility, _spawnManager);
                _controller = new TerritoryController(_settings, _repository, _director, _spawnManager, _stateStore);
                _warfare = new GangWarfareDirector(_repository, _controller);
                _retaliation = new RetaliationDirector(_repository);
                _controller.Retaliation = _retaliation;
                _controller.Warfare = _warfare; // player pressure bleeds the gang's war chest
                _menu = new InteractionMenu(_settings, _controller, _repository, _warfare);

                Functions.OnOnDutyStateChanged += OnOnDutyStateChanged;
                Logger.Info("Loaded with " + _repository.Territories.Count + " territories. Waiting for on-duty.");
            }
            catch (Exception ex)
            {
                Logger.Error("Initialization failed", ex);
            }
        }

        public override void Finally()
        {
            Functions.OnOnDutyStateChanged -= OnOnDutyStateChanged;
            Shutdown();
            Logger.Info("Dynamic Hostile Territories unloaded.");
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
            if (_onDuty || _settings == null || !_settings.Enabled || _controller == null)
                return;

            _onDuty = true;
            _controller.Start();
            _warfare?.Start();
            _retaliation?.Start();

            int gen = ++_inputGen;
            _inputFiber = GameFiber.StartNew(() => InputLoop(gen));
            GameFiber.StartNew(ShowWelcomeNotification);

            Logger.Info("Player went on duty. Systems online.");
        }

        private void Shutdown()
        {
            _onDuty = false;
            _inputGen++;        // invalidate the running input loop so it ends on its own
            _inputFiber = null; // no Abort — the loop sees the flag/generation and exits

            _menu?.Dispose();
            _retaliation?.Stop();
            _warfare?.Stop();
            _controller?.Stop();
        }

        private void InputLoop(int generation)
        {
            while (_onDuty && generation == _inputGen)
            {
                try
                {
                    _menu.Process();
                }
                catch (Exception ex)
                {
                    Logger.Error("Menu loop error", ex);
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
                Logger.Error("Failed to display welcome notification", ex);
            }
        }
    }
}