namespace ServiceLib.ViewModels;

public class MsgViewModel : MyReactiveObject
{
    public Interaction<string, Unit> ShowMsgInteraction { get; } = new();

    private readonly ConcurrentQueue<string> _queueMsg = new();
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
        if (!AutoRefresh)
        {
            return;
        }

        //filter msg
        if (MsgFilter.IsNotEmpty() && !_lastMsgFilterNotAvailable)
        {
            if (!Utils.IsRegexMatch(msg, MsgFilter))
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
