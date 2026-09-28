namespace ServiceLib.ViewModels;

public class MsgViewModel : MyReactiveObject
{
    public Interaction<string, Unit> ShowMsgInteraction { get; } = new();

    private readonly ConcurrentQueue<string> _queueMsg = new();
    private readonly ConnectionCloseNoise _closeNoise = new();
    private volatile bool _lastMsgFilterNotAvailable;
    public int NumMaxMsg { get; } = 500;

    [Reactive]
    public string MsgFilter { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    public MsgViewModel()
    {
        _config = AppManager.Instance.Config;
        MsgFilter = _config.MsgUIItem.MainMsgFilter ?? string.Empty;
        AutoRefresh = _config.MsgUIItem.AutoRefresh ?? true;

        this.WhenAnyValue(
           x => x.MsgFilter)
               .Subscribe(c => DoMsgFilter());

        this.WhenAnyValue(
          x => x.AutoRefresh,
          y => y == true)
              .Subscribe(c => _config.MsgUIItem.AutoRefresh = AutoRefresh);

        AppEvents.SendMsgViewRequested
         .AsObservable()
         .Subscribe(EnqueueQueueMsg);

        this.WhenActivated(disposables =>
        {
            Observable.Interval(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => FlushQueueToView())
                .DisposeWith(disposables);
        });
    }

    private void FlushQueueToView()
    {
        if (!AutoRefresh || !AppManager.Instance.ShowInTaskbar)
        {
            return;
        }

        // Summaries belong only to the unfiltered view. An explicit filter shows
        // matching raw future lines; it does not replay previously collapsed lines.
        if (string.IsNullOrEmpty(MsgFilter))
        {
            var count = _closeNoise.TakeSummaryCount();
            if (count > 0)
            {
                EnqueueWithLimit($"INFO: {count} TCP download-close messages collapsed (endpoint not connected). Original lines: diagnostic log; set a message filter to see future raw lines.{Environment.NewLine}");
            }
        }

        if (_queueMsg.IsEmpty)
        {
            return;
        }

        var sb = new StringBuilder();
        while (_queueMsg.TryDequeue(out var line))
        {
            sb.Append(line);
        }

        if (sb.Length > 0)
        {
            ShowMsgInteraction.Handle(sb.ToString()).Subscribe();
        }
    }

    private void EnqueueQueueMsg(string msg)
    {
        // Any nonempty explicit filter opts out of close-noise collapse, including
        // an invalid regex (the existing matcher fails open in that case).
        var filter = MsgFilter;
        var explicitFilter = !string.IsNullOrEmpty(filter);
        var autoRefresh = AutoRefresh;
        // Preserve original close diagnostics before pause/filter decisions, on
        // the producer thread. Paused display neither queues nor counts messages.
        var collapsed = _closeNoise.TryCollapse(msg, explicitFilter || !autoRefresh);
        if (!autoRefresh || collapsed)
        {
            return;
        }

        if (explicitFilter && !_lastMsgFilterNotAvailable)
        {
            if (!Utils.IsRegexMatch(msg, filter))
            {
                return;
            }
        }

        EnqueueWithLimit(msg);
        if (!msg.EndsWith(Environment.NewLine))
        {
            EnqueueWithLimit(Environment.NewLine);
        }
    }

    private void EnqueueWithLimit(string item)
    {
        _queueMsg.Enqueue(item);

        while (_queueMsg.Count > NumMaxMsg)
        {
            _queueMsg.TryDequeue(out _);
        }
    }

    //public void ClearMsg()
    //{
    //    _queueMsg.Clear();
    //}

    private void DoMsgFilter()
    {
        _config.MsgUIItem.MainMsgFilter = MsgFilter;
        _lastMsgFilterNotAvailable = false;
    }
}
