using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace SSMPEnemyPuppet;

/// <summary>
/// The in-game surface: a hidden MonoBehaviour that ticks the addon and draws its window.
/// </summary>
/// <remarks>
/// SSMP addons are plain objects with no Unity lifecycle, so anything needing a per-frame update,
/// input or IMGUI has to be hosted on a GameObject of its own.
/// </remarks>
internal sealed class PuppetDriver : MonoBehaviour {
    private static PuppetClientAddon _addon;

    private readonly List<string> _log = new();
    private const int MaxLogLines = 6;

    private EnemyInstance _selected;
    private ushort? _selectedEntityId;
    private Vector2 _marker;
    private bool _hasMarker;
    private Vector2 _scroll;
    private bool _open;
    private Rect _window = new(20, 20, 440, 520);

    public static void Install(PuppetClientAddon addon) {
        _addon = addon;

        var host = new GameObject("SSMPEnemyPuppet.Driver");
        DontDestroyOnLoad(host);
        host.AddComponent<PuppetDriver>();
    }

    private void Update() {
        _addon?.Tick();

        if (Input.GetKeyDown(KeyCode.F7)) {
            _open = !_open;
        }

        if (!_open || _addon == null) {
            return;
        }

        bool overWindow = _window.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));

        if (Input.GetMouseButtonDown(1) && !overWindow) {
            PickUnderMouse();
        } else if (Input.GetMouseButtonDown(0) && !overWindow && _selectedEntityId != null) {
            PlaceMarker();
        }
    }

    // ---- input ----------------------------------------------------------------------------

    /// <summary>The world point under the cursor, on the plane enemies stand on.</summary>
    /// <remarks>
    /// The depth has to be supplied: Input.mousePosition carries z = 0, and on a perspective
    /// camera converting at that depth lands every click at the centre of the screen.
    /// </remarks>
    private static Vector3 CursorWorld(Camera cam, float planeZ) {
        Vector3 screen = Input.mousePosition;
        screen.z = Mathf.Abs(cam.transform.position.z - planeZ);
        Vector3 world = cam.ScreenToWorldPoint(screen);
        world.z = planeZ;
        return world;
    }

    private void PickUnderMouse() {
        Camera cam = Camera.main;
        if (cam == null) {
            return;
        }

        float planeZ = EnemyBehavior.ActiveEnemies
            .Where(e => e.IsAlive)
            .Select(e => e.GameObject.transform.position.z)
            .DefaultIfEmpty(0f)
            .First();

        Vector3 world = CursorWorld(cam, planeZ);
        var point = new Vector2(world.x, world.y);

        bool previous = Physics2D.queriesHitTriggers;
        Physics2D.queriesHitTriggers = true;
        try {
            foreach (var hit in Physics2D.OverlapPointAll(point, ~0)) {
                var owner = OwningEnemy(hit != null ? hit.transform : null);
                if (owner != null) {
                    Select(owner);
                    return;
                }
            }
        } finally {
            Physics2D.queriesHitTriggers = previous;
        }

        EnemyInstance best = null;
        float bestDistance = 2.5f;
        foreach (var enemy in EnemyBehavior.ActiveEnemies) {
            if (!enemy.IsAlive) {
                continue;
            }

            float d = Vector2.Distance(point, enemy.GameObject.transform.position);
            if (d < bestDistance) {
                bestDistance = d;
                best = enemy;
            }
        }

        if (best != null) {
            Select(best);
        }
    }

    private static EnemyInstance OwningEnemy(Transform t) {
        for (; t != null; t = t.parent) {
            var found = EnemyBehavior.GetInstance(t.gameObject);
            if (found != null) {
                return found;
            }
        }

        return null;
    }

    private void Select(EnemyInstance enemy) {
        _selected = enemy;
        _selectedEntityId = null;
        Note($"selected {enemy.Profile?.EnemyId}");
    }

    private void PlaceMarker() {
        Camera cam = Camera.main;
        if (cam == null || _selected == null || !_selected.IsAlive) {
            return;
        }

        Vector3 world = CursorWorld(cam, _selected.GameObject.transform.position.z);
        _marker = new Vector2(world.x, world.y);
        _hasMarker = true;

        _addon.SendCommand(_selectedEntityId.Value, PuppetCommandKind.WalkTo, _marker, AimPoint());
    }

    /// <summary>
    /// Where an attack should be pointed: the real opposing player, not the walk marker.
    /// </summary>
    /// <remarks>
    /// Falls back to the marker in single player, so the rig stays usable for testing alone.
    /// </remarks>
    private Vector2 AimPoint() {
        if (_selected == null) {
            return _marker;
        }

        return _addon.NearestOpponent(_selected.GameObject.transform.position) ?? _marker;
    }

    private void Note(string message) {
        _log.Add($"[{Time.time:0.0}] {message}");
        if (_log.Count > MaxLogLines) {
            _log.RemoveAt(0);
        }

        Log.Info(message);
    }

    // ---- UI -------------------------------------------------------------------------------

    private void OnGUI() {
        if (!_open) {
            return;
        }

        DrawMarker();
        _window = GUI.Window(GetInstanceID(), _window, DrawWindow, "SSMP Enemy Puppet (F7)");
    }

    private void DrawMarker() {
        Camera cam = Camera.main;
        if (!_hasMarker || cam == null) {
            return;
        }

        Vector3 p = cam.WorldToScreenPoint(_marker);
        if (p.z < 0f) {
            return;
        }

        var at = new Vector2(p.x, Screen.height - p.y);
        Color previous = GUI.color;
        GUI.color = new Color(1f, 0.85f, 0.2f, 0.9f);

        const float arm = 11f, thick = 2f;
        GUI.DrawTexture(new Rect(at.x - arm, at.y - thick * 0.5f, arm * 2f, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(at.x - thick * 0.5f, at.y - arm, thick, arm * 2f), Texture2D.whiteTexture);

        GUI.color = previous;
    }

    private void DrawWindow(int id) {
        GUILayout.Label($"claims: {_addon.Registry.Count}   opponents in scene: {_addon.Opponents().Count()}");
        GUILayout.Label("RIGHT-CLICK an enemy to select   LEFT-CLICK to move it");

        var enemies = EnemyBehavior.ActiveEnemies.Where(e => e.IsAlive).ToList();
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(110));
        foreach (var enemy in enemies) {
            bool isSelected = _selected != null && _selected.InstanceId == enemy.InstanceId;
            if (GUILayout.Button($"{(isSelected ? "> " : "  ")}{enemy.Profile?.EnemyId}", GUILayout.Height(20))) {
                Select(enemy);
            }
        }

        GUILayout.EndScrollView();

        GUILayout.Space(6);

        if (_selected == null || !_selected.IsAlive) {
            GUILayout.Label("Nothing selected.");
        } else if (_selectedEntityId == null) {
            if (GUILayout.Button("Take control")) {
                if (_addon.RequestControl(_selected.GameObject)) {
                    Note("claim sent - waiting for the scene host");
                }
            }

            // The claim is granted by the scene host, so the id only appears once its reply lands.
            var claim = _addon.Registry.Claims.FirstOrDefault(c => c.PlayerId == _addon.LocalPlayerId());
            if (claim.EntityId != 0) {
                _selectedEntityId = claim.EntityId;
            }
        } else {
            GUILayout.Label($"driving entity {_selectedEntityId} - team {_addon.Registry.TeamFor(_selectedEntityId.Value)}");

            if (GUILayout.Button("Release")) {
                _addon.ReleaseControl(_selectedEntityId.Value);
                _selectedEntityId = null;
            }

            GUILayout.Space(4);
            GUILayout.Label("Attacks - aimed at the other player:");
            DrawAttacks();
        }

        GUILayout.Space(6);
        foreach (string line in _log) {
            GUILayout.Label(line);
        }

        GUI.DragWindow();
    }

    private void DrawAttacks() {
        var profile = _selected?.Profile;
        if (profile == null || profile.Actions.Count == 0) {
            GUILayout.Label("   (no attacks discovered)");
            return;
        }

        int column = 0;
        GUILayout.BeginHorizontal();
        foreach (var attack in profile.Actions.OrderByDescending(a => a.Confidence)) {
            if (GUILayout.Button($"{attack.DisplayName}\n{attack.Shape}", GUILayout.Height(32))) {
                _addon.SendCommand(
                    _selectedEntityId.Value, PuppetCommandKind.Fire, _marker, AimPoint(), attack.Id);
                Note($"fire {attack.DisplayName}");
            }

            if (++column % 2 == 0) {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
            }
        }

        GUILayout.EndHorizontal();
    }
}
