namespace ProstoKVN;

public enum SubscriptionAccessFailure { DeviceLimit, DeviceIdentity, UnsupportedClient }

public sealed class SubscriptionAccessException : InvalidOperationException
{
    public SubscriptionAccessFailure Failure { get; }
    public SubscriptionAccessException(SubscriptionAccessFailure failure) : base(failure switch
    {
        SubscriptionAccessFailure.DeviceLimit => "Провайдер сообщает: превышен лимит устройств. Удалите неиспользуемое устройство в приложении или боте провайдера и повторите импорт.",
        SubscriptionAccessFailure.DeviceIdentity => "Провайдер не принял идентификатор устройства. Проверьте доступ к подписке у провайдера.",
        _ => "Провайдер вернул «App not supported» или служебную заглушку вместо серверов. Проверьте поддержку клиента у провайдера."
    }) { Failure = failure; }

    public static void CheckPlaceholderNodes(IReadOnlyList<ProxyNode> nodes)
    {
        if (nodes.Count == 0 || !nodes.All(n => n.Values.TryGetValue("server", out var address)
            && Convert.ToString(address) is "0.0.0.0" or "::")) return;
        bool limit = nodes.Any(n => n.Name.Contains("лимит устройств", StringComparison.OrdinalIgnoreCase)
            || n.Name.Contains("device limit", StringComparison.OrdinalIgnoreCase));
        throw new SubscriptionAccessException(limit ? SubscriptionAccessFailure.DeviceLimit
            : SubscriptionAccessFailure.UnsupportedClient);
    }
}
