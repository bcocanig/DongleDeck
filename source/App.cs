using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DongleDeck {
    public sealed class MainWindow {
        readonly Window window;
        readonly ComboBox receiverBox;
        readonly ItemsControl deviceList,candidateList;
        readonly FrameworkElement emptyPanel,pairPanel;
        readonly TextBlock meta,capacity,notice,bottom,instructions,searchStatus,countdown,activity,scanState,pairText,pairGlyph;
        readonly Border noticeBorder;
        readonly Button refresh,clear,pairButton,themeButton;
        readonly ProgressBar operationProgress;
        readonly CheckBox automatic;
        readonly ObservableCollection<DeviceSlot> rows=new ObservableCollection<DeviceSlot>();
        readonly ObservableCollection<Candidate> candidates=new ObservableCollection<Candidate>();
        readonly Dictionary<string,DeviceSlot> telemetry=new Dictionary<string,DeviceSlot>();
        readonly DispatcherTimer activityTimer=new DispatcherTimer();
        List<ReceiverSnapshot> snapshots=new List<ReceiverSnapshot>();
        ReceiverSnapshot current;
        ReceiverWatcher watcher;
        RawActivityMonitor rawActivity;
        bool busy,updating,finishing,closeAfter,listening,scanAnimated;
        CancellationTokenSource cancellation;
        PairingSession pairing;
        readonly DateTime[] lastInputUi=new DateTime[3];
        string operationPhase="Ready";
        DateTime phaseStarted=DateTime.UtcNow;
        public MainWindow() {
            using(Stream xaml=Assembly.GetExecutingAssembly().GetManifestResourceStream("DongleDeck.App.xaml")) window=(Window)XamlReader.Load(xaml);
            receiverBox=Find<ComboBox>("ReceiverBox");deviceList=Find<ItemsControl>("DeviceList");candidateList=Find<ItemsControl>("CandidateList");
            emptyPanel=Find<FrameworkElement>("EmptyPanel");pairPanel=Find<FrameworkElement>("PairPanel");
            meta=Find<TextBlock>("ReceiverMeta");capacity=Find<TextBlock>("CapacityText");notice=Find<TextBlock>("NoticeText");bottom=Find<TextBlock>("BottomStatus");
            instructions=Find<TextBlock>("PairInstructions");searchStatus=Find<TextBlock>("SearchStatus");countdown=Find<TextBlock>("CountdownText");activity=Find<TextBlock>("ActivityText");noticeBorder=Find<Border>("NoticeBorder");
            refresh=Find<Button>("RefreshButton");clear=Find<Button>("ClearButton");pairButton=Find<Button>("PairButton");automatic=Find<CheckBox>("AutoPairCheck");
            operationProgress=Find<ProgressBar>("OperationProgress");
            scanState=Find<TextBlock>("ScanStateText");pairText=Find<TextBlock>("PairButtonText");pairGlyph=Find<TextBlock>("PairButtonGlyph");themeButton=Find<Button>("ThemeButton");
            using(Stream icon=Assembly.GetExecutingAssembly().GetManifestResourceStream("DongleDeck.ico")) {var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=icon;image.EndInit();window.Icon=image;}
            ApplyTheme(Theme.Load(),false);
            themeButton.Click+=delegate {ApplyTheme(!Theme.IsDark,true);};
            deviceList.ItemsSource=rows;candidateList.ItemsSource=candidates;
            receiverBox.SelectionChanged+=delegate {if(!updating) {StopWatcher();var selected=receiverBox.SelectedItem as ReceiverInfo;current=selected==null?null:snapshots.FirstOrDefault(s=>s.Receiver.Key==selected.Key);Render();StartWatcher();}};
            refresh.Click+=async delegate {await Refresh();};
            pairButton.Click+=async delegate {if(listening) {if(cancellation!=null) cancellation.Cancel();searchStatus.Text=finishing?"Completing current device…":"Stopping…";scanState.Text=searchStatus.Text;pairButton.IsEnabled=false;SetPhase(searchStatus.Text);}else await Pair();};
            automatic.Checked+=delegate {UpdateAutomatic();};automatic.Unchecked+=delegate {UpdateAutomatic();};
            clear.Click+=async delegate {await Clear();};
            deviceList.AddHandler(Button.ClickEvent,new RoutedEventHandler(async delegate(object sender,RoutedEventArgs e) {
                var button=FindButton(e.OriginalSource as DependencyObject);
                if(button!=null && button.Tag is DeviceSlot) {e.Handled=true;await Remove((DeviceSlot)button.Tag);}
            }));
            candidateList.AddHandler(Button.ClickEvent,new RoutedEventHandler(delegate(object sender,RoutedEventArgs e) {
                var button=FindButton(e.OriginalSource as DependencyObject);
                if(button!=null && button.Tag is Candidate && pairing!=null) {var candidate=(Candidate)button.Tag;if(!candidate.AlreadyPaired && !finishing) {finishing=true;candidateList.IsEnabled=false;pairing.Select(candidate);searchStatus.Text="Pairing "+candidate.Model+"…";SetPhase(searchStatus.Text);countdown.Text="";}e.Handled=true;}
            }));
            window.SourceInitialized+=delegate {rawActivity=new RawActivityMonitor(window,OnInput);};
            window.Loaded+=async delegate {await Refresh();};
            window.Closing+=delegate(object sender,CancelEventArgs e) {if(busy) {e.Cancel=true;closeAfter=true;if(cancellation!=null) cancellation.Cancel();bottom.Text="Finishing receiver operation before closing…";}};
            window.Closed+=delegate {activityTimer.Stop();StopWatcher();if(rawActivity!=null) rawActivity.Dispose();};
            activityTimer.Interval=TimeSpan.FromMilliseconds(250);activityTimer.Tick+=delegate {foreach(var row in rows) row.NotifyTelemetry();if(busy) bottom.Text=operationPhase+"  ·  "+(int)(DateTime.UtcNow-phaseStarted).TotalSeconds+"s";};activityTimer.Start();
        }
        T Find<T>(string name) where T:class {return (T)window.FindName(name);}
        static Button FindButton(DependencyObject child) {while(child!=null) {var button=child as Button;if(button!=null) return button;child=VisualTreeHelper.GetParent(child);}return null;}
        public void Show() {window.Show();}
        void ApplyTheme(bool dark,bool save) {
            Theme.Apply(window,dark,save);themeButton.Content=dark?"Light mode":"Dark mode";
            using(Stream logo=Assembly.GetExecutingAssembly().GetManifestResourceStream(dark?"DongleDeck.LogoDark.png":"DongleDeck.LogoLight.png")) {
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=logo;image.EndInit();Find<Image>("LogoImage").Source=image;
            }
            foreach(var row in rows) row.NotifyTelemetry();
        }
        void SetPhase(string status) {if(operationPhase!=status) {operationPhase=status;phaseStarted=DateTime.UtcNow;}bottom.Text=status;}
        void SetBusy(bool value,string status) {busy=value;SetPhase(status);operationProgress.Visibility=value?Visibility.Visible:Visibility.Collapsed;operationProgress.IsIndeterminate=value;RenderControls();if(!value && closeAfter) window.Close();}
        void RenderControls() {
            refresh.IsEnabled=!busy;receiverBox.IsEnabled=!busy;
            bool available=current!=null && !busy;
            pairButton.IsEnabled=listening || (available && current.Slots.Count<6);
            pairText.Text=listening?"Stop pairing":"Start pairing";pairGlyph.Text=listening?"■":"▶";
            System.Windows.Automation.AutomationProperties.SetName(pairButton,pairText.Text);
            pairButton.Background=pairButton.BorderBrush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(listening?"#B33D4D":"#237446"));
            scanState.Text=listening?"Scanning · click Stop pairing when you’re done":"Scanning is off";
            scanState.SetResourceReference(TextBlock.ForegroundProperty,listening?"Success":"Muted");
            if(scanAnimated!=listening) {
                scanAnimated=listening;int index=0;
                foreach(string name in new[]{"WaveNear","WaveMid","WaveFar"}) {
                    var wave=Find<System.Windows.Shapes.Path>(name);
                    if(listening) wave.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0.15,1,new Duration(TimeSpan.FromSeconds(0.65))) {AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,BeginTime=TimeSpan.FromSeconds(index++*0.2)});
                    else {wave.BeginAnimation(UIElement.OpacityProperty,null);wave.Opacity=0.25;}
                }
            }
            clear.IsEnabled=available && current.Slots.Count>0;deviceList.IsEnabled=!busy;
        }
        void Render() {
            rows.Clear();if(current!=null) foreach(var slot in current.Slots.OrderBy(s=>s.Number)) rows.Add(slot);
            emptyPanel.Visibility=rows.Count>0?Visibility.Collapsed:Visibility.Visible;
            meta.Text=current==null?"Supports Dell Universal receiver 413C:301C · Refresh after plugging in.":"USB "+current.Receiver.UsbId+"  ·  Revision "+current.Receiver.Revision;
            capacity.Text=current==null?"":current.Slots.Count+" of 6 slots used";RenderControls();
        }
        void Notice(string message,bool error) {
            notice.Text=message;notice.SetResourceReference(TextBlock.ForegroundProperty,error?"Danger":"Success");
            noticeBorder.SetResourceReference(Border.BackgroundProperty,error?"DangerSoft":"SuccessSoft");AppLog.Raw(message);
        }
        void UpdateAutomatic() {
            if(pairing!=null) pairing.Automatic=automatic.IsChecked==true;
            candidateList.Visibility=automatic.IsChecked==true?Visibility.Collapsed:Visibility.Visible;
            instructions.Text="Blue receiver channel → power off → hold a key/button → power on.\n"+(automatic.IsChecked==true?"Devices pair automatically.":"Choose a device below.");
        }
        async Task Refresh() {
            if(busy) return;string key=current==null?null:current.Receiver.Key;SetBusy(true,"Reading receiver…");StopWatcher();
            try {await Reload(key);Notice(current==null?"No supported receiver found. Plug in a Dell Universal dongle and click Refresh.":"Ready.",false);}
            catch(Exception ex) {Notice(ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }
        async Task Reload(string key) {
            var results=await Task.Run(()=>ReceiverOperations.Scan());
            foreach(var snapshot in results) foreach(var slot in snapshot.Slots) {
                string id=snapshot.Receiver.Key+"/"+slot.Identity;DeviceSlot old;
                if(telemetry.TryGetValue(id,out old)) slot.CopyTelemetry(old);telemetry[id]=slot;
            }
            snapshots=results;updating=true;receiverBox.ItemsSource=snapshots.Select(s=>s.Receiver).ToList();
            current=snapshots.FirstOrDefault(s=>s.Receiver.Key==key)??snapshots.FirstOrDefault();receiverBox.SelectedItem=current==null?null:current.Receiver;updating=false;Render();
        }
        void StopWatcher() {if(watcher!=null) {watcher.Dispose();watcher=null;}}
        void StartWatcher() {
            StopWatcher();if(current==null || listening || closeAfter) return;
            string key=current.Receiver.Key;ReceiverWatcher created=null;
            try {
                created=new ReceiverWatcher(current.Receiver,signal=>window.Dispatcher.BeginInvoke(new Action(delegate {
                    if(watcher!=created || current==null || current.Receiver.Key!=key) return;
                    var slot=current.Slots.FirstOrDefault(s=>s.Number==signal.Slot);if(slot==null) return;
                    if(signal.Online.HasValue) {slot.ReportedOnline=signal.Online;if(signal.Online==true) slot.LastSeenUtc=signal.AtUtc;else slot.LastInputUtc=DateTime.MinValue;}
                    if(signal.Battery.HasValue) {slot.BatteryPercent=signal.Battery;if(slot.ReportedOnline==true) slot.LastSeenUtc=signal.AtUtc;}
                    slot.NotifyTelemetry();activity.Text="";
                })));
                watcher=created;
            } catch(Exception ex) {AppLog.Raw("Live status unavailable: "+ex.Message);activity.Text="Live status unavailable.";}
        }
        void OnInput(string physical,byte kind) {
            if(current==null || current.Receiver.PhysicalDeviceId==null || current.Receiver.PhysicalDeviceId!=physical || (DateTime.UtcNow-lastInputUi[kind]).TotalMilliseconds<180) return;
            lastInputUi[kind]=DateTime.UtcNow;
            var matching=current.Slots.Where(s=>s.Kind==kind).ToList();
            var active=matching.Where(s=>s.ReportedOnline==true).ToList();
            DeviceSlot exact=matching.Count==1?matching[0]:(active.Count==1 && matching.All(s=>s==active[0] || s.ReportedOnline==false)?active[0]:null);
            if(exact!=null) {exact.ReportedOnline=true;exact.LastSeenUtc=DateTime.UtcNow;exact.LastInputUtc=DateTime.UtcNow;exact.NotifyTelemetry();activity.Text="";}
            else activity.Text=(kind==1?"Keyboard":"Mouse")+" activity on this receiver. Switch one device off/on to identify its exact row.";
        }
        async Task Pair() {
            if(busy || current==null) return;var receiver=current.Receiver;
            StopWatcher();listening=true;pairPanel.Visibility=Visibility.Visible;UpdateAutomatic();cancellation=new CancellationTokenSource();
            SetBusy(true,"Pairing mode on");
            try {
                while(!cancellation.IsCancellationRequested && current!=null && current.Receiver.Key==receiver.Key && current.Slots.Count<6) {
                    candidates.Clear();candidateList.IsEnabled=true;finishing=false;searchStatus.Text="Opening receiver…";SetPhase(searchStatus.Text);countdown.Text="";
                    pairing=new PairingSession {Automatic=automatic.IsChecked==true};
                    var progress=new Progress<PairProgress>(p=> {
                        if(p.Candidate!=null) candidates.Add(p.Candidate);
                        searchStatus.Text=p.Selecting?p.Message:(automatic.IsChecked==true?"Waiting for a keyboard or mouse…":p.Message);
                        SetPhase(searchStatus.Text);
                        countdown.Text=p.Selecting?"Saving pairing…":"Continuous scan";
                        if(p.Selecting) {finishing=true;candidateList.IsEnabled=false;}
                    });
                    DeviceSlot added=await Task.Run(()=>pairing.Run(receiver,0,progress,cancellation.Token));
                    if(added!=null) {
                        Notice(added.Model+" paired · slot "+added.Number+".",false);SetPhase("Updating devices…");
                        await Reload(receiver.Key);
                        var row=current==null?null:current.Slots.FirstOrDefault(s=>s.Id==added.Id && s.Kind==added.Kind);
                        if(row!=null) {row.ReportedOnline=true;row.LastSeenUtc=DateTime.UtcNow;row.NotifyTelemetry();}
                    }
                }
                if(current!=null && current.Slots.Count==6) Notice("All six slots are used. Remove a device to make room.",false);
            } catch(OperationCanceledException) {Notice("Pairing mode stopped.",false);} catch(Exception ex) {Notice(ex.Message,true);}
            finally {pairPanel.Visibility=Visibility.Collapsed;pairing=null;cancellation.Dispose();cancellation=null;finishing=false;listening=false;}
            try {await Reload(receiver.Key);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }
        async Task Remove(DeviceSlot slot) {
            if(busy || current==null) return;var receiver=current.Receiver;StopWatcher();SetBusy(true,"Removing device…");
            try {current.Slots=await Task.Run(()=>ReceiverOperations.Remove(receiver,slot));Render();Notice(slot.Model+" removed.",false);}
            catch(Exception ex) {Notice(ex.Message,true);}
            try {await Reload(receiver.Key);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }
        async Task Clear() {
            if(busy || current==null) return;var receiver=current.Receiver;var targets=current.Slots.ToList();StopWatcher();SetBusy(true,"Clearing pairings…");
            try {current.Slots=await Task.Run(()=>ReceiverOperations.Clear(receiver,targets));Render();Notice("Pairings cleared.",false);}
            catch(Exception ex) {Notice(ex.Message,true);}
            try {await Reload(receiver.Key);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }
    }
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            bool created;using(var mutex=new Mutex(true,"Local\\DongleDeck.Utility",out created)) {
                if(!created) {MessageBox.Show("DongleDeck is already open.");return 1;}
                if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) {MessageBox.Show("Run DongleDeck as administrator so it can manage the Dell service.");return 1;}
                AppLog.Raw("DongleDeck 0.4.0 started");
                var app=new Application();app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e) {AppLog.Raw(e.Exception.ToString());MessageBox.Show(e.Exception.Message,"DongleDeck");e.Handled=true;};
                new MainWindow().Show();return app.Run();
            }
        }
    }
}
