using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace SSMPEnemyPuppet;

/// <summary>
/// Applies puppet commands to a real enemy, on the machine that has authority over it.
/// </summary>
/// <remarks>
/// Only ever runs on the scene host. SSMP replicates enemy position, animation and FSM state from
/// the host outward, so a command applied anywhere else is overwritten by the next sync; routing
/// everything here means the change happens where authority already lives and replicates by the
/// mechanism SSMP already provides.
/// </remarks>
internal sealed class PuppetExecutor {
    /// <summary>How close counts as arrived, so a puppet stops instead of jittering on the spot.</summary>
    private const float ArriveDistance = 1.2f;

    private const float RefireSeconds = 0.2f;

    /// <summary>
    /// How long the hero is held at the aim point after an attack is fired.
    /// </summary>
    /// <remarks>
    /// Attacks do not read the target once and commit. They sample it over several frames - a
    /// wind-up turns to face, a projectile computes its velocity on a later frame - so snapping the
    /// hero there for a single frame aims roughly half of them at wherever she was before.
    /// </remarks>
    private const float AimHoldSeconds = 0.6f;

    private readonly Dictionary<ushort, PuppetState> _states = new();
    private HeroController _hero;

    private Vector3 _aimPoint;
    private float _aimUntil;
    private Vector3 _markerPoint;
    private bool _hasMarker;

    private sealed class PuppetState {
        public IEnemyHandle Handle;
        public EnemyInstance Enemy;
        public MovementDescriptor Walk;
        public MovementDescriptor Idle;
        public Vector2 Target;
        public bool Walking;
        public float NextFireAt;
    }

    /// <summary>Begin driving an entity. Idempotent.</summary>
    public bool Attach(ushort entityId, GameObject enemyObject, string ownerId) {
        if (_states.ContainsKey(entityId)) {
            return true;
        }

        var handle = EnemyBehavior.Claim(enemyObject, ownerId, AuthorityTier.Override);
        if (handle == null) {
            Log.Warn($"entity {entityId}: the Enemy Behavior API would not give a handle");
            return false;
        }

        if (handle.Tier != AuthorityTier.Override) {
            Log.Warn($"entity {entityId}: only got {handle.Tier}, another mod holds Override");
            handle.Dispose();
            return false;
        }

        // Decisions suppressed so the enemy stops choosing for itself, but not everything -
        // SuppressAll would veto the transition into its own walk state, and also its death.
        handle.Policy = OverridePolicy.SuppressDecisions;

        var profile = handle.Profile;

        _states[entityId] = new PuppetState {
            Handle = handle,
            Enemy = EnemyBehavior.GetInstance(enemyObject),
            // Untargeted walk preferred: a chase state would drag the enemy at its own target on
            // its own terms, which is exactly the authority being taken away from it.
            Walk = PickWalk(profile, targeted: false) ?? PickWalk(profile, targeted: true),
            Idle = profile?.Movements
                .Where(m => m.Mode == MovementMode.Idle)
                .OrderByDescending(m => m.Confidence)
                .FirstOrDefault(),
            Target = enemyObject.transform.position
        };

        Log.Info($"entity {entityId}: driving '{profile?.EnemyId}' " +
                 $"({profile?.Actions.Count ?? 0} attacks, walk='{_states[entityId].Walk?.DisplayName ?? "none"}')");
        return true;
    }

    /// <summary>Stop driving an entity and hand it back.</summary>
    public void Detach(ushort entityId) {
        if (!_states.TryGetValue(entityId, out var state)) {
            return;
        }

        state.Handle?.Dispose();
        _states.Remove(entityId);
        Log.Info($"entity {entityId}: released");
    }

    /// <summary>Stop driving everything.</summary>
    public void DetachAll() {
        foreach (var id in _states.Keys.ToList()) {
            Detach(id);
        }
    }

    public bool IsDriving(ushort entityId) => _states.ContainsKey(entityId);

    /// <summary>Apply one command. Called on the host, for commands from any player.</summary>
    public void Apply(ushort entityId, PuppetCommandKind kind, Vector2 point, Vector2 aim, string behaviorId) {
        if (!_states.TryGetValue(entityId, out var state) || state.Handle == null || !state.Handle.IsValid) {
            return;
        }

        switch (kind) {
            case PuppetCommandKind.WalkTo:
                state.Target = point;
                state.Walking = true;
                break;

            case PuppetCommandKind.Halt:
                state.Walking = false;
                break;

            case PuppetCommandKind.Fire:
                // Point the hero at the opponent before firing, so the attack aims at a real
                // player rather than at the walk marker.
                SetAim(aim);
                if (!state.Handle.Fire(behaviorId)) {
                    Log.Debug($"entity {entityId}: fire '{behaviorId}' was refused");
                }

                break;
        }
    }

    /// <summary>Where the walk marker should sit when nothing is being aimed at.</summary>
    public void SetMarker(Vector2 point) {
        _markerPoint = new Vector3(point.x, point.y, _markerPoint.z);
        _hasMarker = true;
    }

    private void SetAim(Vector2 aim) {
        _aimPoint = new Vector3(aim.x, aim.y, _aimPoint.z);
        _aimUntil = Time.time + AimHoldSeconds;
    }

    /// <summary>
    /// Step every puppet. Call once per frame on the host.
    /// </summary>
    public void Tick() {
        EnsureHero();

        // The hero doubles as both the walk marker and the aim point, because enemy attacks are
        // built to target Hornet and nothing else. She sits on the marker normally, and jumps to
        // the opponent for the moment an attack needs to be aimed.
        if (_hero != null) {
            if (Time.time < _aimUntil) {
                _hero.transform.position = _aimPoint;
            } else if (_hasMarker) {
                _hero.transform.position = _markerPoint;
            }
        }

        foreach (var entry in _states) {
            Step(entry.Key, entry.Value);
        }
    }

    private void Step(ushort entityId, PuppetState state) {
        if (state.Enemy == null || !state.Enemy.IsAlive || state.Walk == null || !state.Walking) {
            return;
        }

        Vector3 here = state.Enemy.GameObject.transform.position;
        float dx = state.Target.x - here.x;

        if (Mathf.Abs(dx) <= ArriveDistance) {
            if (state.Idle != null && !IsActive(state.Enemy, state.Idle) && Time.time >= state.NextFireAt) {
                state.NextFireAt = Time.time + RefireSeconds;
                state.Handle.Fire(state.Idle.Id);
            }

            return;
        }

        Face(state.Enemy.GameObject.transform, dx);

        if (!IsActive(state.Enemy, state.Walk) && Time.time >= state.NextFireAt) {
            state.NextFireAt = Time.time + RefireSeconds;
            state.Handle.Fire(state.Walk.Id);
        }
    }

    private static bool IsActive(EnemyInstance enemy, BehaviorDescriptor behavior) {
        var fsm = enemy.ResolveFsm(behavior.State);
        return fsm != null && fsm.ActiveStateName == behavior.State.StateName;
    }

    /// <summary>Point an enemy along a direction by the sign of its local X scale.</summary>
    /// <remarks>
    /// The same channel the enemy's own FSM turns with, so its scale-relative movement actions
    /// then carry it the right way.
    /// </remarks>
    private static void Face(Transform t, float direction) {
        Vector3 s = t.localScale;
        float magnitude = Mathf.Abs(s.x);
        s.x = direction > 0f ? magnitude : -magnitude;
        t.localScale = s;
    }

    private static MovementDescriptor PickWalk(EnemyProfile profile, bool targeted) {
        if (profile == null) {
            return null;
        }

        foreach (var mode in new[] { MovementMode.Walk, MovementMode.Fly, MovementMode.Chase }) {
            var found = profile.Movements
                .Where(m => m.Mode == mode && m.IsTargeted == targeted)
                .OrderByDescending(m => m.Confidence)
                .FirstOrDefault();

            if (found != null) {
                return found;
            }
        }

        return null;
    }

    private void EnsureHero() {
        if (_hero == null) {
            _hero = Object.FindObjectOfType<HeroController>();
        }
    }
}
