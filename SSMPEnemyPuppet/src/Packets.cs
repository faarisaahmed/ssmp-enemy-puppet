using System.Collections.Generic;
using SSMP.Networking.Packet;

namespace SSMPEnemyPuppet;

/// <summary>
/// Packet IDs for this addon's private channel.
/// </summary>
/// <remarks>
/// Every command travels client -> scene host, and every result travels host -> everyone. That is
/// not a stylistic choice: SSMP's entity system makes the scene host authoritative for enemy
/// position, animation and FSM state, so an enemy driven locally by a non-host is overwritten by
/// the host's next sync. Routing commands through the host means the change happens where authority
/// lives and then replicates by the mechanism that already exists.
/// </remarks>
internal enum PuppetPacketId : byte {
    /// <summary>Client -> host: I would like to drive this entity.</summary>
    ClaimRequest = 0,

    /// <summary>Client -> host: I am done with this entity.</summary>
    ReleaseRequest = 1,

    /// <summary>Host -> everyone: the current controller of each claimed entity.</summary>
    ClaimState = 2,

    /// <summary>Client -> host: make my puppet do something.</summary>
    Command = 3
}

/// <summary>What a <see cref="CommandPacket"/> is asking for.</summary>
internal enum PuppetCommandKind : byte {
    /// <summary>Walk toward a point. Carries the marker position.</summary>
    WalkTo = 0,

    /// <summary>Stop walking and idle.</summary>
    Halt = 1,

    /// <summary>Fire a behaviour by its Enemy Behavior API descriptor id.</summary>
    Fire = 2
}

/// <summary>Who is driving one entity.</summary>
internal struct PuppetClaim {
    public ushort EntityId;

    /// <summary>SSMP player id of the controller.</summary>
    public ushort PlayerId;

    /// <summary>The controller's SSMP team, so hits can be attributed without a second lookup.</summary>
    public byte Team;
}

/// <summary>Client -> host: claim or release one entity.</summary>
internal class ClaimPacket : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    /// <remarks>
    /// Never superseded. A claim and a release are distinct events, and dropping either leaves
    /// an entity stuck owned by someone who has stopped driving it.
    /// </remarks>
    public bool DropReliableDataIfNewerExists => false;

    public ushort EntityId { get; set; }

    /// <summary>
    /// Who sent this. Stamped by the server on relay, never trusted from the client.
    /// </summary>
    /// <remarks>
    /// It has to travel in the packet because SSMP hands client-side handlers the packet alone -
    /// only server handlers receive a sender id - and the machine that arbitrates claims is a
    /// client (the scene host), not the server.
    /// </remarks>
    public ushort PlayerId { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(EntityId);
        packet.Write(PlayerId);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        EntityId = packet.ReadUShort();
        PlayerId = packet.ReadUShort();
    }
}

/// <summary>
/// Host -> everyone: the full set of current claims.
/// </summary>
/// <remarks>
/// Sent whole rather than as deltas, and superseded by any newer copy, because only the latest
/// picture matters and a missed delta would leave a client's UI permanently disagreeing about who
/// owns what.
/// </remarks>
internal class ClaimStatePacket : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;

    public List<PuppetClaim> Claims { get; set; } = new();

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write((ushort) Claims.Count);

        foreach (var claim in Claims) {
            packet.Write(claim.EntityId);
            packet.Write(claim.PlayerId);
            packet.Write(claim.Team);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Claims = new List<PuppetClaim>();

        var count = packet.ReadUShort();
        for (var i = 0; i < count; i++) {
            Claims.Add(new PuppetClaim {
                EntityId = packet.ReadUShort(),
                PlayerId = packet.ReadUShort(),
                Team = packet.ReadByte()
            });
        }
    }
}

/// <summary>
/// Client -> host: one instruction for the sender's puppet.
/// </summary>
/// <remarks>
/// <see cref="PuppetCommandKind.WalkTo"/> is superseded by a newer copy - only the latest
/// destination matters, and a queue of stale waypoints would make steering feel like it was
/// fighting you. <see cref="PuppetCommandKind.Fire"/> must not be, since each one is a distinct
/// attack the player chose to commit to, so this packet is reliable and never dropped; the host
/// discards superseded walk targets on arrival instead.
/// </remarks>
internal class CommandPacket : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    public ushort EntityId { get; set; }

    /// <inheritdoc cref="ClaimPacket.PlayerId"/>
    public ushort PlayerId { get; set; }

    public PuppetCommandKind Kind { get; set; }

    /// <summary>Marker position for <see cref="PuppetCommandKind.WalkTo"/>.</summary>
    public float X { get; set; }

    /// <inheritdoc cref="X"/>
    public float Y { get; set; }

    /// <summary>Descriptor id for <see cref="PuppetCommandKind.Fire"/>.</summary>
    public string BehaviorId { get; set; } = string.Empty;

    /// <summary>
    /// Where the firing player's own Hornet is, so the attack aims at a real opponent rather than
    /// at the walk marker.
    /// </summary>
    /// <remarks>
    /// The two are different points on purpose. The marker says where the puppet should stand;
    /// the aim point is the actual position of the player being attacked, which is the only thing
    /// a projectile or a lunge should be pointed at.
    /// </remarks>
    public float AimX { get; set; }

    /// <inheritdoc cref="AimX"/>
    public float AimY { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(EntityId);
        packet.Write(PlayerId);
        packet.Write((byte) Kind);
        packet.Write(X);
        packet.Write(Y);
        packet.Write(AimX);
        packet.Write(AimY);
        packet.Write(BehaviorId ?? string.Empty);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        EntityId = packet.ReadUShort();
        PlayerId = packet.ReadUShort();
        Kind = (PuppetCommandKind) packet.ReadByte();
        X = packet.ReadFloat();
        Y = packet.ReadFloat();
        AimX = packet.ReadFloat();
        AimY = packet.ReadFloat();
        BehaviorId = packet.ReadString();
    }
}
