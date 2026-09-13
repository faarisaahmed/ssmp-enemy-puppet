using System.Linq;
using SSMP.Api.Server;
using SSMP.Api.Server.Networking;
using SSMP.Networking.Packet;

namespace SSMPEnemyPuppet;

/// <summary>
/// Relays puppet traffic between players, and stamps each packet with who really sent it.
/// </summary>
/// <remarks>
/// Two jobs, both forced by how SSMP is shaped.
///
/// It relays because authority over enemies belongs to the *scene host* - the player whose machine
/// is simulating the room - not to the server process, which may not be running that scene at all.
/// Commands therefore have to reach another client, and client-to-client traffic goes through here.
///
/// It stamps because SSMP hands client-side packet handlers the packet alone; only server handlers
/// receive a sender id. Without the server writing the true sender into the packet, the scene host
/// would have to believe whatever id a client claimed, and any client could drive an enemy someone
/// else was using by lying about who it was.
/// </remarks>
public class PuppetServerAddon : ServerAddon {
    /// <inheritdoc />
    protected override string Name => "EnemyPuppet";

    /// <inheritdoc />
    protected override string Version => "0.1.0";

    /// <inheritdoc />
    public override bool NeedsNetwork => true;

    /// <inheritdoc />
    /// <remarks>A literal rather than Addon.CurrentApiVersion, so a future SSMP can refuse this
    /// addon rather than load it against an API it was not written for.</remarks>
    public override uint ApiVersion => 1;

    private IServerApi _api;
    private IServerAddonNetworkSender<PuppetPacketId> _sender;

    /// <inheritdoc />
    public override void Initialize(IServerApi serverApi) {
        _api = serverApi;
        Log.Initialize(Logger);

        _sender = serverApi.NetServer.GetNetworkSender<PuppetPacketId>(this);

        var receiver = serverApi.NetServer.GetNetworkReceiver<PuppetPacketId>(this, InstantiatePacket);

        receiver.RegisterPacketHandler<ClaimPacket>(
            PuppetPacketId.ClaimRequest,
            (id, packet) => { packet.PlayerId = id; Relay(id, PuppetPacketId.ClaimRequest, packet); });

        receiver.RegisterPacketHandler<ClaimPacket>(
            PuppetPacketId.ReleaseRequest,
            (id, packet) => { packet.PlayerId = id; Relay(id, PuppetPacketId.ReleaseRequest, packet); });

        receiver.RegisterPacketHandler<CommandPacket>(
            PuppetPacketId.Command,
            (id, packet) => { packet.PlayerId = id; Relay(id, PuppetPacketId.Command, packet); });

        // Claim state originates from the scene host and is pure broadcast, so it needs no stamping.
        receiver.RegisterPacketHandler<ClaimStatePacket>(
            PuppetPacketId.ClaimState,
            (id, packet) => Relay(id, PuppetPacketId.ClaimState, packet));

        Log.Info("Server relay ready.");
    }

    private static IPacketData InstantiatePacket(PuppetPacketId packetId) => packetId switch {
        PuppetPacketId.ClaimRequest => new ClaimPacket(),
        PuppetPacketId.ReleaseRequest => new ClaimPacket(),
        PuppetPacketId.ClaimState => new ClaimStatePacket(),
        PuppetPacketId.Command => new CommandPacket(),
        _ => null
    };

    /// <summary>
    /// Forward to every other player in the sender's scene.
    /// </summary>
    /// <remarks>
    /// Scene-scoped rather than addressed to the scene host specifically, because SSMP can move
    /// scene hosting between clients when someone leaves and the server would have to track that.
    /// Recipients filter by role instead: only the scene host acts on a request or a command, and
    /// only non-hosts act on a claim-state broadcast.
    /// </remarks>
    private void Relay(ushort senderId, PuppetPacketId packetId, IPacketData packet) {
        var sender = _api.ServerManager.GetPlayer(senderId);
        if (sender == null || string.IsNullOrEmpty(sender.CurrentScene)) {
            return;
        }

        var recipients = _api.ServerManager.Players
            .Where(player => player.Id != senderId && player.CurrentScene == sender.CurrentScene)
            .Select(player => player.Id)
            .ToArray();

        if (recipients.Length == 0) {
            return;
        }

        _sender.SendSingleData(packetId, packet, recipients);
    }
}
