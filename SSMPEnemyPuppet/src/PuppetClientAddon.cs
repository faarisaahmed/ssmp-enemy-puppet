using System;
using System.Collections.Generic;
using System.Linq;
using SSMP.Api.Client;
using SSMP.Api.Client.Networking;
using SSMP.Networking.Packet;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMPEnemyPuppet;

/// <summary>
/// Lets both players take control of enemies and fight each other with them.
/// </summary>
/// <remarks>
/// Sits on top of two things. SSMPEnemySync makes enemies shared and authoritative on the scene
/// host; the Enemy Behavior API supplies each enemy's discovered attacks and movement states. This
/// addon adds ownership and routing: who is driving which enemy, and how a command from the player
/// who is not the scene host reaches the machine that can actually carry it out.
///
/// Everything flows client -> scene host -> the world -> SSMP's existing entity sync. There is no
/// path where a non-host drives an enemy locally, because the host's next sync would overwrite it.
/// </remarks>
public class PuppetClientAddon : ClientAddon {
    /// <inheritdoc />
    protected override string Name => "EnemyPuppet";

    /// <inheritdoc />
    protected override string Version => "0.1.0";

    /// <inheritdoc />
    public override bool NeedsNetwork => true;

    /// <inheritdoc />
    public override uint ApiVersion => 1;

    /// <summary>How often the host republishes the claim table, in seconds.</summary>
    private const float ClaimBroadcastInterval = 1f;

    private IClientApi _api;
    private object _entityManager;

    private readonly PuppetRegistry _registry = new();
    private readonly PuppetExecutor _executor = new();

    private IClientAddonNetworkSender<PuppetPacketId> _sender;
    private float _nextClaimBroadcast;
    private string _scene = string.Empty;

    /// <summary>Entity ids this player has asked to drive, whether or not the host has agreed.</summary>
    private readonly HashSet<ushort> _wanted = new();

    /// <inheritdoc />
    public override void Initialize(IClientApi clientApi) {
        _api = clientApi;
        Log.Initialize(Logger);

        SsmpReflection.Initialize();
        if (!SsmpReflection.Available) {
            Log.Error($"disabled: {SsmpReflection.UnavailableReason}. This addon reads SSMP internals " +
                      "and is tied to a specific SSMP version.");
            return;
        }

        if (!EnemyBehaviorApiBridge.Available) {
            Log.Error("disabled: the Enemy Behavior API is not loaded. Install it alongside SSMP.");
            return;
        }

        var receiver = _api.NetClient.GetNetworkReceiver<PuppetPacketId>(this, InstantiatePacket);
        receiver.RegisterPacketHandler<ClaimStatePacket>(PuppetPacketId.ClaimState, OnClaimState);
        receiver.RegisterPacketHandler<ClaimPacket>(PuppetPacketId.ClaimRequest, OnClaimRequest);
        receiver.RegisterPacketHandler<ClaimPacket>(PuppetPacketId.ReleaseRequest, OnReleaseRequest);
        receiver.RegisterPacketHandler<CommandPacket>(PuppetPacketId.Command, OnCommand);

        _sender = _api.NetClient.GetNetworkSender<PuppetPacketId>(this);

        _api.ClientManager.ConnectEvent += OnConnect;
        _api.ClientManager.DisconnectEvent += OnDisconnect;
        _api.ClientManager.PlayerDisconnectEvent += OnPlayerDisconnect;

        PuppetDriver.Install(this);

        Log.Info("Initialised.");
    }

    private static IPacketData InstantiatePacket(PuppetPacketId id) => id switch {
        PuppetPacketId.ClaimRequest => new ClaimPacket(),
        PuppetPacketId.ReleaseRequest => new ClaimPacket(),
        PuppetPacketId.ClaimState => new ClaimStatePacket(),
        PuppetPacketId.Command => new CommandPacket(),
        _ => null
    };

    // ---- lifecycle ------------------------------------------------------------------------

    private void OnConnect() {
        _entityManager = SsmpReflection.GetEntityManager(_api.ClientManager);
        Log.Info("Connected; entity manager acquired.");
    }

    private void OnDisconnect() {
        _executor.DetachAll();
        _registry.Clear();
        _wanted.Clear();
        _entityManager = null;
    }

    /// <summary>
    /// Drop everything a departing player held.
    /// </summary>
    /// <remarks>
    /// Without this, someone who alt-F4s mid-fight leaves their puppet owned forever and nobody
    /// else can ever claim it.
    /// </remarks>
    private void OnPlayerDisconnect(IClientPlayer player) {
        if (!IsSceneHost()) {
            return;
        }

        var dropped = _registry.ReleaseAllFor(player.Id);
        if (dropped <= 0) {
            return;
        }

        foreach (var claim in _registry.Claims.ToList()) {
            if (claim.PlayerId == player.Id) {
                _executor.Detach(claim.EntityId);
            }
        }

        Log.Info($"player {player.Id} left; released {dropped} puppet(s).");
        BroadcastClaims();
    }

    // ---- per-frame ------------------------------------------------------------------------

    /// <summary>Driven by <see cref="PuppetDriver"/>, since addons get no Update of their own.</summary>
    internal void Tick() {
        if (_entityManager == null) {
            return;
        }

        // Entity ids are reassigned per scene, so a claim table that survives a scene change points
        // at the wrong enemies entirely.
        string scene = SceneManager.GetActiveScene().name;
        if (scene != _scene) {
            _scene = scene;
            _executor.DetachAll();
            _registry.Clear();
            _wanted.Clear();
        }

        if (!IsSceneHost()) {
            return;
        }

        _executor.Tick();

        if (Time.time >= _nextClaimBroadcast) {
            _nextClaimBroadcast = Time.time + ClaimBroadcastInterval;
            BroadcastClaims();
        }
    }

    private bool IsSceneHost() =>
        _entityManager != null &&
        SsmpReflection.IsSceneHostDetermined(_entityManager) &&
        SsmpReflection.IsSceneHost(_entityManager);

    // ---- local player actions -------------------------------------------------------------

    /// <summary>Ask to drive the enemy behind a scene object.</summary>
    internal bool RequestControl(GameObject enemyObject) {
        var id = SsmpReflection.FindEntityId(_entityManager, enemyObject);
        if (id == null) {
            Log.Warn("that object is not a synced entity - SSMPEnemySync may not recognise it as an enemy.");
            return false;
        }

        _wanted.Add(id.Value);

        if (IsSceneHost()) {
            _learnedLocalId ??= 0;
            HandleClaim(id.Value, LocalPlayerId(), LocalTeam());
        } else {
            _sender.SendSingleData(PuppetPacketId.ClaimRequest,
                new ClaimPacket { EntityId = id.Value, PlayerId = LocalPlayerId() });
        }

        return true;
    }

    /// <summary>Give up an enemy.</summary>
    internal void ReleaseControl(ushort entityId) {
        _wanted.Remove(entityId);

        if (IsSceneHost()) {
            HandleRelease(entityId, LocalPlayerId());
        } else {
            _sender.SendSingleData(PuppetPacketId.ReleaseRequest,
                new ClaimPacket { EntityId = entityId, PlayerId = LocalPlayerId() });
        }
    }

    /// <summary>Send one instruction for an enemy this player is driving.</summary>
    /// <param name="aim">
    /// Where the opponent actually is. Kept separate from the walk marker on purpose: the marker
    /// says where the puppet should stand, the aim point is what an attack should be pointed at.
    /// </param>
    internal void SendCommand(ushort entityId, PuppetCommandKind kind, Vector2 point, Vector2 aim, string behaviorId = "") {
        if (!_registry.IsControlledBy(entityId, LocalPlayerId())) {
            return;
        }

        if (IsSceneHost()) {
            if (kind == PuppetCommandKind.WalkTo) {
                _executor.SetMarker(point);
            }

            _executor.Apply(entityId, kind, point, aim, behaviorId);
            return;
        }

        _sender.SendSingleData(PuppetPacketId.Command, new CommandPacket {
            EntityId = entityId,
            PlayerId = LocalPlayerId(),
            Kind = kind,
            X = point.x,
            Y = point.y,
            AimX = aim.x,
            AimY = aim.y,
            BehaviorId = behaviorId ?? string.Empty
        });
    }

    /// <summary>Where the other players are, for aiming. Empty in single player.</summary>
    internal IEnumerable<IClientPlayer> Opponents() =>
        _api?.ClientManager?.Players?.Where(p => p.IsInLocalScene && p.PlayerObject != null)
        ?? Enumerable.Empty<IClientPlayer>();

    /// <summary>The nearest opponent's position, or null if nobody else is here.</summary>
    internal Vector2? NearestOpponent(Vector3 from) {
        IClientPlayer best = null;
        float bestDistance = float.MaxValue;

        foreach (var player in Opponents()) {
            float d = Vector2.Distance(from, player.PlayerObject.transform.position);
            if (d >= bestDistance) {
                continue;
            }

            bestDistance = d;
            best = player;
        }

        return best == null ? null : (Vector2?) (Vector2) best.PlayerObject.transform.position;
    }

    internal PuppetRegistry Registry => _registry;

    /// <summary>
    /// Our own player id, learned from the first claim the host grants us.
    /// </summary>
    /// <remarks>
    /// SSMP exposes no local player id - only a username - so it cannot simply be read. But
    /// <c>ClientManager.Players</c> lists only *remote* players, so any id in the claim table that
    /// belongs to no remote player is necessarily ours. Until we have claimed something there is
    /// nothing to learn from, which is fine: with nothing claimed there is nothing to check.
    /// </remarks>
    private ushort? _learnedLocalId;

    internal ushort LocalPlayerId() => _learnedLocalId ?? 0;

    /// <summary>True if this id belongs to no remote player, and is therefore ours.</summary>
    private bool IsLocal(ushort playerId) {
        try {
            return _api.ClientManager.Players.All(p => p.Id != playerId);
        } catch (Exception) {
            return false;
        }
    }

    private void LearnLocalId() {
        if (_learnedLocalId != null) {
            return;
        }

        foreach (var claim in _registry.Claims) {
            if (_wanted.Contains(claim.EntityId) && IsLocal(claim.PlayerId)) {
                _learnedLocalId = claim.PlayerId;
                Log.Debug($"local player id learned: {claim.PlayerId}");
                return;
            }
        }
    }

    private byte LocalTeam() {
        try {
            return (byte) (int) _api.ClientManager.Team;
        } catch (Exception) {
            return 0;
        }
    }

    // ---- host-side arbitration ------------------------------------------------------------

    private void OnClaimRequest(ClaimPacket packet) {
        ushort playerId = packet.PlayerId;
        if (!IsSceneHost()) {
            return;
        }

        var player = _api.ClientManager.Players.FirstOrDefault(p => p.Id == playerId);
        byte team = player == null ? (byte) 0 : (byte) (int) player.Team;
        HandleClaim(packet.EntityId, playerId, team);
    }

    private void OnReleaseRequest(ClaimPacket packet) {
        ushort playerId = packet.PlayerId;
        if (!IsSceneHost()) {
            return;
        }

        HandleRelease(packet.EntityId, playerId);
    }

    private void HandleClaim(ushort entityId, ushort playerId, byte team) {
        if (!_registry.TryClaim(entityId, playerId, team)) {
            Log.Info($"entity {entityId}: claim by player {playerId} refused, already driven.");
            BroadcastClaims();
            return;
        }

        var obj = SsmpReflection.FindEntityObject(_entityManager, entityId);
        if (obj == null || !_executor.Attach(entityId, obj, AddonOwnerId)) {
            _registry.Release(entityId, playerId);
            Log.Warn($"entity {entityId}: could not attach, claim rolled back.");
        }

        BroadcastClaims();
    }

    private void HandleRelease(ushort entityId, ushort playerId) {
        if (!_registry.Release(entityId, playerId)) {
            return;
        }

        _executor.Detach(entityId);
        BroadcastClaims();
    }

    private void OnCommand(CommandPacket packet) {
        ushort playerId = packet.PlayerId;
        if (!IsSceneHost()) {
            return;
        }

        // Only the holder may drive it. Without this check any client could puppet any enemy,
        // including one the other player is in the middle of using.
        if (!_registry.IsControlledBy(packet.EntityId, playerId)) {
            return;
        }

        if (packet.Kind == PuppetCommandKind.WalkTo) {
            _executor.SetMarker(new Vector2(packet.X, packet.Y));
        }

        _executor.Apply(
            packet.EntityId,
            packet.Kind,
            new Vector2(packet.X, packet.Y),
            new Vector2(packet.AimX, packet.AimY),
            packet.BehaviorId);
    }

    private void OnClaimState(ClaimStatePacket packet) {
        _registry.ReplaceAll(packet.Claims);
        LearnLocalId();
    }

    private void BroadcastClaims() {
        if (!IsSceneHost()) {
            return;
        }

        _sender.SendSingleData(PuppetPacketId.ClaimState, new ClaimStatePacket {
            Claims = _registry.Claims.ToList()
        });
    }

    /// <summary>Owner id handed to the Enemy Behavior API, so conflicts name this addon.</summary>
    internal const string AddonOwnerId = "com.faaris.ssmpenemypuppet";
}
