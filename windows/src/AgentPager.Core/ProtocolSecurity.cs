using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentPager.Core;

public enum ProtocolFailure { InvalidVersion, Expired, Replayed, InvalidSignature }

public sealed class ProtocolException(ProtocolFailure failure) : Exception(failure.ToString())
{
    public ProtocolFailure Failure { get; } = failure;
}

public static class ControlSigner
{
    /// <summary>
    /// 签名串里的字符串必须与 **App 端 kotlinx.serialization** 的转义**逐字节一致**。
    ///
    /// 默认的 <see cref="JsonSerializer"/> 会把非 ASCII 转义成 \uXXXX（还会转 &lt; &gt; &amp; ' 等），
    /// 而 kotlinx 原样输出——两边算出来的 HMAC 就不同，校验静默失败。
    /// **实测（2026-10-08）踩过**：手机作答（含中文文本）点了没反应，日志里连 `Perform` 都没进；
    /// 批准/拒绝一直正常，只因为它们的 `value` 是 null、根本不走这段。
    /// </summary>
    private static readonly JsonSerializerOptions Canonical = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool Verify(SignedControlEnvelope envelope, byte[] secret)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { return false; }
        using var hmac = new HMACSHA256(secret);
        var expected = hmac.ComputeHash(SigningData(envelope));
        return CryptographicOperations.FixedTimeEquals(signature, expected);
    }

    public static byte[] SigningData(SignedControlEnvelope envelope)
    {
        var action = JsonSerializer.Serialize(WireAction(envelope.Payload.Action), Canonical);
        var task = JsonSerializer.Serialize(envelope.Payload.TaskID, Canonical);
        var payload = envelope.Payload.Value is null
            ? $"{{\"action\":{action},\"taskID\":{task}}}"
            : $"{{\"action\":{action},\"taskID\":{task},\"value\":{JsonSerializer.Serialize(envelope.Payload.Value, Canonical)}}}";
        var text = string.Join('\n',
            envelope.Version.ToString(),
            envelope.MessageId.ToString().ToLowerInvariant(),
            envelope.SentAt.ToString(),
            envelope.DeviceId,
            envelope.Sequence.ToString(),
            envelope.Nonce,
            envelope.Type,
            payload);
        return Encoding.UTF8.GetBytes(text);
    }

    private static string WireAction(ControlAction action)
    {
        var name = action.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

public sealed class ReplayGuard(
    TimeSpan? maxClockSkew = null,
    TimeSpan? nonceLifetime = null)
{
    private readonly Dictionary<string, ulong> _highestSequence = [];
    private readonly Dictionary<string, DateTimeOffset> _nonces = [];
    private readonly TimeSpan _maxClockSkew = maxClockSkew ?? TimeSpan.FromSeconds(90);
    private readonly TimeSpan _nonceLifetime = nonceLifetime ?? TimeSpan.FromMinutes(5);

    public void Validate(SignedControlEnvelope envelope, byte[] secret, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        if (envelope.Version != 1) throw new ProtocolException(ProtocolFailure.InvalidVersion);
        var sent = DateTimeOffset.FromUnixTimeMilliseconds(envelope.SentAt);
        if ((current - sent).Duration() > _maxClockSkew)
            throw new ProtocolException(ProtocolFailure.Expired);

        foreach (var expired in _nonces.Where(item => current - item.Value > _nonceLifetime).Select(item => item.Key).ToList())
            _nonces.Remove(expired);
        if (_nonces.ContainsKey(envelope.Nonce) ||
            envelope.Sequence <= _highestSequence.GetValueOrDefault(envelope.DeviceId))
            throw new ProtocolException(ProtocolFailure.Replayed);
        if (!ControlSigner.Verify(envelope, secret))
            throw new ProtocolException(ProtocolFailure.InvalidSignature);

        _nonces[envelope.Nonce] = current;
        _highestSequence[envelope.DeviceId] = envelope.Sequence;
    }
}
