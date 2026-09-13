using System.Collections.Generic;
using System.Linq;

namespace SSMPEnemyPuppet;

/// <summary>
/// Who is driving which entity, and on whose team.
/// </summary>
/// <remarks>
/// Held identically on every machine: the scene host owns the truth and broadcasts it, and clients
/// replace their copy wholesale from each <see cref="ClaimStatePacket"/>. Keeping it a plain map
/// rather than scattering ownership through the puppet code means the damage layer - which does not
/// exist yet - has one place to ask "who gets credit for this enemy's hit".
/// </remarks>
internal sealed class PuppetRegistry {
    private readonly Dictionary<ushort, PuppetClaim> _claims = new();

    /// <summary>Every current claim.</summary>
    public IEnumerable<PuppetClaim> Claims => _claims.Values;

    public int Count => _claims.Count;

    /// <summary>The claim on an entity, or null if nobody is driving it.</summary>
    public PuppetClaim? Get(ushort entityId) =>
        _claims.TryGetValue(entityId, out var claim) ? claim : null;

    /// <summary>True when this player is driving this entity.</summary>
    public bool IsControlledBy(ushort entityId, ushort playerId) =>
        _claims.TryGetValue(entityId, out var claim) && claim.PlayerId == playerId;

    /// <summary>True when somebody other than this player is driving it.</summary>
    public bool IsContested(ushort entityId, ushort playerId) =>
        _claims.TryGetValue(entityId, out var claim) && claim.PlayerId != playerId;

    /// <summary>The team credited with this entity's hits, or null if unclaimed.</summary>
    /// <remarks>
    /// The whole point of tracking team alongside controller: contact damage from a driven enemy
    /// should count for whoever is driving it, and an unclaimed enemy belongs to nobody, so it
    /// should keep hurting everyone as it normally would.
    /// </remarks>
    public byte? TeamFor(ushort entityId) =>
        _claims.TryGetValue(entityId, out var claim) ? claim.Team : null;

    /// <summary>Host side: record a claim. Returns false if someone else already holds it.</summary>
    public bool TryClaim(ushort entityId, ushort playerId, byte team) {
        if (_claims.TryGetValue(entityId, out var existing) && existing.PlayerId != playerId) {
            return false;
        }

        _claims[entityId] = new PuppetClaim { EntityId = entityId, PlayerId = playerId, Team = team };
        return true;
    }

    /// <summary>Host side: drop a claim, but only for the player that holds it.</summary>
    public bool Release(ushort entityId, ushort playerId) {
        if (!_claims.TryGetValue(entityId, out var existing) || existing.PlayerId != playerId) {
            return false;
        }

        _claims.Remove(entityId);
        return true;
    }

    /// <summary>
    /// Host side: drop everything a player held.
    /// </summary>
    /// <remarks>
    /// Called on disconnect and on scene change. Without it a player who alt-F4s mid-fight leaves
    /// their puppet owned forever, and nobody else can ever claim it.
    /// </remarks>
    public int ReleaseAllFor(ushort playerId) {
        var owned = _claims.Where(kv => kv.Value.PlayerId == playerId).Select(kv => kv.Key).ToList();
        foreach (var id in owned) {
            _claims.Remove(id);
        }

        return owned.Count;
    }

    /// <summary>Drop every claim. Used on scene change, where entity ids are reassigned.</summary>
    public void Clear() => _claims.Clear();

    /// <summary>Client side: replace the local picture with the host's.</summary>
    public void ReplaceAll(IEnumerable<PuppetClaim> claims) {
        _claims.Clear();
        foreach (var claim in claims) {
            _claims[claim.EntityId] = claim;
        }
    }
}
