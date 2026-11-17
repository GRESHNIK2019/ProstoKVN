using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ProstoKVN;

public partial class MainWindow
{
    private ServerLatencyProbe serverLatencyProbe = new();
    private CancellationTokenSource latencySession = new();
    private CancellationTokenSource? latencyBatch;
    private int latencyRevision, latencyCompleted, latencyTotal;
    public ServerLatencyProbe? LatencyProbeOverride { get; set; }

    private void ResetServerLatencySession()
    {
        ++latencyRevision;
        latencyBatch?.Cancel();latencyBatch=null;
        latencySession.Cancel();latencySession=new();
        serverLatencyProbe=new();latencyCompleted=latencyTotal=0;
        UpdateLatencyUi();
    }
    private void UpdateLatencyUi()
    {
        if(PingSelectedButton==null || ServerLatencyHint==null)return;
        var selected=ServerBox.SelectedItem as ProxyNode;
        PingSelectedButton.IsEnabled=selected!=null && !selected.IsLatencyChecking;
        PingAllButton.IsEnabled=nodes.Count>0;
        PingAllButton.Content=latencyBatch!=null?"Остановить":"Проверить все";
        if(latencyBatch!=null)
            ServerLatencyHint.Text=$"Проверено {latencyCompleted} из {latencyTotal} · выбор сервера не меняется";
        else if(selected?.IsLatencyChecking==true)ServerLatencyHint.Text="Измеряем отклик выбранного сервера…";
        else if(selected?.Latency is { } result)
            ServerLatencyHint.Text=$"{(result.Method==ServerLatencyMethod.Tcp?"TCP-подключение":"ICMP-пинг")} · обновлено {result.MeasuredUtc.ToLocalTime():HH:mm:ss}";
        else ServerLatencyHint.Text="Отклик до сервера · TCP / ICMP. Не является проверкой работы VPN.";
        ServerLatencyHint.ToolTip=selected?.LatencyDescription??"Добавьте подписку и выберите сервер для проверки.";
    }
    private async void PingSelected_Click(object sender,RoutedEventArgs e)
    {
        if(ServerBox.SelectedItem is ProxyNode node)
            await CheckServerLatencyAsync(node,latencyBatch?.Token??latencySession.Token);
    }
    private async void PingServerRow_Click(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is FrameworkElement{DataContext:ProxyNode node} && nodes.Contains(node))
            await CheckServerLatencyAsync(node,latencyBatch?.Token??latencySession.Token);
    }
    private async Task CheckServerLatencyAsync(ProxyNode node,CancellationToken cancellation)
    {
        if(node.IsLatencyChecking || isWindowClosing || cancellation.IsCancellationRequested)return;
        int revision=latencyRevision;
        node.BeginLatencyCheck();UpdateLatencyUi();
        try
        {
            var result=await (LatencyProbeOverride??serverLatencyProbe).MeasureAsync(node,cancellation);
            node.CompleteLatencyCheck(revision==latencyRevision && !isWindowClosing?result:null);
        }
        catch(OperationCanceledException){node.CompleteLatencyCheck(null);}
        catch(Exception ex)
        {
            bool current=revision==latencyRevision && !isWindowClosing;
            node.CompleteLatencyCheck(current?new(ServerLatencyProbe.MethodFor(node),null,DateTime.UtcNow,"Не удалось выполнить проверку отклика."):null);
            if(current)AppendLog("Проверка отклика: "+ex.GetType().Name);
        }
        finally{if(revision==latencyRevision && !isWindowClosing)UpdateLatencyUi();}
    }
    private async void PingAll_Click(object sender,RoutedEventArgs e)
    {
        if(latencyBatch!=null){latencyBatch.Cancel();PingAllButton.Content="Отмена…";return;}
        var snapshot=nodes.Where(n=>!n.IsLatencyChecking).ToArray();
        if(snapshot.Length==0 || isWindowClosing)return;
        var batch=CancellationTokenSource.CreateLinkedTokenSource(latencySession.Token);
        latencyBatch=batch;serverLatencyProbe=new();
        latencyCompleted=0;latencyTotal=snapshot.Length;UpdateLatencyUi();
        try
        {
            await Task.WhenAll(snapshot.Select(async node=>
            {
                await CheckServerLatencyAsync(node,batch.Token);
                if(ReferenceEquals(latencyBatch,batch) && !isWindowClosing)
                {++latencyCompleted;UpdateLatencyUi();}
            }));
        }
        finally
        {
            if(ReferenceEquals(latencyBatch,batch))
            {
                latencyBatch=null;
                if(!isWindowClosing)
                {
                    UpdateLatencyUi();
                    if(batch.IsCancellationRequested)ServerLatencyHint.Text="Проверка остановлена. Полученные результаты сохранены.";
                }
            }
            batch.Dispose();
        }
    }
}
