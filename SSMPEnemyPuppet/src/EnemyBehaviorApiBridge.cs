using System;
using EnemyBehaviorApi;
using EnemyBehaviorApi.Schema;

namespace SSMPEnemyPuppet;

/// <summary>
/// Guards every use of the Enemy Behavior API behind one availability check.
/// </summary>
/// <remarks>
/// The API is a hard dependency but a separate BepInEx plugin, so it can be missing, or present at
/// a schema version this addon was not written against. Touching it before checking would throw a
/// TypeLoadException during SSMP's addon initialisation, which takes multiplayer down with it -
/// a far worse outcome than this addon declining to load.
/// </remarks>
internal static class EnemyBehaviorApiBridge {
    private static bool _checked;
    private static bool _available;

    /// <summary>Whether the API is present and speaks a schema version this addon understands.</summary>
    public static bool Available {
        get {
            if (_checked) {
                return _available;
            }

            _checked = true;

            try {
                _available = EnemyBehavior.IsReady && SchemaVersion.IsCompatible(SchemaVersion.Current);

                if (!_available) {
                    Log.Warn(EnemyBehavior.IsReady
                        ? $"Enemy Behavior API reports schema v{EnemyBehavior.Version}, which this addon does not support."
                        : "Enemy Behavior API is installed but not ready.");
                }
            } catch (Exception e) {
                _available = false;
                Log.Warn($"Enemy Behavior API unavailable: {e.GetType().Name}");
            }

            return _available;
        }
    }
}
