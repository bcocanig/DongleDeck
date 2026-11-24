using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
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
        readonly FrameworkElement emptyPanel,pairPanel,comboLockPanel;
        readonly TextBlock meta,capacity,notice,bottom,instructions,searchStatus,countdown,activity,scanState,pairText,pairGlyph,pairedSubtitle,emptySubtitle,pairPanelSubtitle,comboDetectText,receiverLockBannerText;
        readonly Border noticeBorder,ddpmBorder,receiverLockBanner;
        readonly TextBlock ddpmTitle,ddpmDetail,ddpmGlyph;
        readonly Button refresh,clear,pairButton,themeButton,lockReceiverButton,lockComboButton,confirmComboLockButton,cancelComboLockButton;
        readonly ProgressBar operationProgress;
        readonly CheckBox automatic,jabraOnlyCheck,lockReceiverTooCheck;
        readonly TextBox ownerLabelBox;
        readonly ObservableCollection<DeviceSlot> rows=new ObservableCollection<DeviceSlot>();
        readonly ObservableCollection<Candidate> candidates=new ObservableCollection<Candidate>();
        readonly Dictionary<string,DeviceSlot> telemetry=new Dictionary<string,DeviceSlot>();
        readonly HashSet<string> knownReceiverKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer activityTimer=new DispatcherTimer();
        readonly DispatcherTimer hotplugTimer=new DispatcherTimer();
        List<ReceiverSnapshot> snapshots=new List<ReceiverSnapshot>();
        ReceiverSnapshot current;
        ReceiverWatcher watcher;
        RawActivityMonitor rawActivity;
        DeviceChangeMonitor hotplug;
        bool topologyPending,checkingTopology;
        bool busy,updating,finishing,closeAfter,listening,scanAnimated,comboClaimActive,selfTestMode;
        CancellationTokenSource cancellation;
        PairingSession pairing;
        readonly DateTime[] lastInputUi=new DateTime[3];
        ReceiverSnapshot detectedComboReceiver;
        DeviceSlot detectedComboKeyboard,detectedComboMouse;
        string operationPhase="Ready";
        DateTime phaseStarted=DateTime.UtcNow;

        public MainWindow() {
            using(Stream xaml=Assembly.GetExecutingAssembly().GetManifestResourceStream("DongleDeck.App.xaml")) window=(Window)XamlReader.Load(xaml);
            receiverBox=Find<ComboBox>("ReceiverBox");deviceList=Find<ItemsControl>("DeviceList");candidateList=Find<ItemsControl>("CandidateList");
            emptyPanel=Find<FrameworkElement>("EmptyPanel");pairPanel=Find<FrameworkElement>("PairPanel");comboLockPanel=Find<FrameworkElement>("ComboLockPanel");
            meta=Find<TextBlock>("ReceiverMeta");capacity=Find<TextBlock>("CapacityText");notice=Find<TextBlock>("NoticeText");bottom=Find<TextBlock>("BottomStatus");
            instructions=Find<TextBlock>("PairInstructions");searchStatus=Find<TextBlock>("SearchStatus");countdown=Find<TextBlock>("CountdownText");activity=Find<TextBlock>("ActivityText");noticeBorder=Find<Border>("NoticeBorder");
            ddpmBorder=Find<Border>("DdpmStatusBorder");ddpmTitle=Find<TextBlock>("DdpmStatusTitle");ddpmDetail=Find<TextBlock>("DdpmStatusDetail");ddpmGlyph=Find<TextBlock>("DdpmStatusGlyph");
            receiverLockBanner=Find<Border>("ReceiverLockBanner");receiverLockBannerText=Find<TextBlock>("ReceiverLockBannerText");
            pairedSubtitle=Find<TextBlock>("PairedSubtitle");emptySubtitle=Find<TextBlock>("EmptySubtitle");pairPanelSubtitle=Find<TextBlock>("PairPanelSubtitle");comboDetectText=Find<TextBlock>("ComboDetectText");
            refresh=Find<Button>("RefreshButton");clear=Find<Button>("ClearButton");pairButton=Find<Button>("PairButton");automatic=Find<CheckBox>("AutoPairCheck");
            jabraOnlyCheck=Find<CheckBox>("JabraOnlyCheck");lockReceiverTooCheck=Find<CheckBox>("LockReceiverTooCheck");ownerLabelBox=Find<TextBox>("OwnerLabelBox");
            lockReceiverButton=Find<Button>("LockReceiverButton");lockComboButton=Find<Button>("LockComboButton");confirmComboLockButton=Find<Button>("ConfirmComboLockButton");cancelComboLockButton=Find<Button>("CancelComboLockButton");
            operationProgress=Find<ProgressBar>("OperationProgress");
            scanState=Find<TextBlock>("ScanStateText");pairText=Find<TextBlock>("PairButtonText");pairGlyph=Find<TextBlock>("PairButtonGlyph");themeButton=Find<Button>("ThemeButton");
            using(Stream icon=Assembly.GetExecutingAssembly().GetManifestResourceStream("DongleDeck.ico")) window.Icon=BitmapFrame.Create(icon,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            ApplyTheme(Theme.Load(),false);
            themeButton.Click+=delegate {ApplyTheme(!Theme.IsDark,true);};
            deviceList.ItemsSource=rows;candidateList.ItemsSource=candidates;
            receiverBox.SelectionChanged+=delegate {if(!updating) {StopWatcher();var selected=receiverBox.SelectedItem as ReceiverInfo;current=selected==null?null:snapshots.FirstOrDefault(s=>s.Receiver.Key==selected.Key);Render();StartWatcher();}};
            refresh.Click+=async delegate {await Refresh(false);};
            pairButton.Click+=async delegate {if(listening) {if(cancellation!=null) cancellation.Cancel();searchStatus.Text=finishing?"Completing current device\u2026":"Stopping\u2026";scanState.Text=searchStatus.Text;pairButton.IsEnabled=false;SetPhase(searchStatus.Text);}else await Pair();};
            automatic.Checked+=delegate {UpdateAutomatic();};automatic.Unchecked+=delegate {UpdateAutomatic();};
            jabraOnlyCheck.Checked+=async delegate {if(!busy && current!=null && current.Receiver.IsWindowsBluetooth) await Refresh(false);};
            jabraOnlyCheck.Unchecked+=async delegate {if(!busy && current!=null && current.Receiver.IsWindowsBluetooth) await Refresh(false);};
            clear.Click+=async delegate {await Clear();};
            lockReceiverButton.Click+=delegate {ToggleCurrentReceiverLock();};
            lockComboButton.Click+=delegate {ToggleComboLockPanel();};
            confirmComboLockButton.Click+=delegate {ConfirmComboLock();};
            cancelComboLockButton.Click+=delegate {CloseComboLockPanel();};
            lockReceiverTooCheck.Checked+=delegate {if(comboClaimActive) UpdateComboDetectStatus();};
            lockReceiverTooCheck.Unchecked+=delegate {if(comboClaimActive) UpdateComboDetectStatus();};

            deviceList.AddHandler(Button.ClickEvent,new RoutedEventHandler(async delegate(object sender,RoutedEventArgs e) {
                var button=FindButton(e.OriginalSource as DependencyObject);
                if(button==null || !(button.Tag is DeviceSlot)) return;
                var slot=(DeviceSlot)button.Tag;
                e.Handled=true;
                if(button.Name=="LockBtn") ToggleDeviceLock(slot);
                else if(button.Name=="ConnectBtn") await ToggleConnect(slot);
                else await Remove(slot);
            }));
            candidateList.AddHandler(Button.ClickEvent,new RoutedEventHandler(delegate(object sender,RoutedEventArgs e) {
                var button=FindButton(e.OriginalSource as DependencyObject);
                if(button!=null && button.Tag is Candidate && pairing!=null) {var candidate=(Candidate)button.Tag;if(!candidate.AlreadyPaired && !finishing) {finishing=true;candidateList.IsEnabled=false;pairing.Select(candidate);searchStatus.Text="Pairing "+candidate.Model+"\u2026";SetPhase(searchStatus.Text);countdown.Text="";}e.Handled=true;}
            }));
            JabraManager.Default.TopologyChanged+=delegate {
                if(!closeAfter) window.Dispatcher.BeginInvoke(new Action(delegate {
                    topologyPending=true;hotplugTimer.Stop();hotplugTimer.Start();
                }));
            };
            window.SourceInitialized+=delegate {rawActivity=new RawActivityMonitor(window,OnInput);hotplug=new DeviceChangeMonitor(window,OnHardwareChanged);};
            window.Loaded+=async delegate {if(!selfTestMode) await Refresh(false);};
            window.Closing+=delegate(object sender,CancelEventArgs e) {if(busy) {e.Cancel=true;closeAfter=true;if(cancellation!=null) cancellation.Cancel();bottom.Text="Finishing receiver operation before closing\u2026";}};
            window.Closed+=delegate {activityTimer.Stop();hotplugTimer.Stop();StopWatcher();if(hotplug!=null) hotplug.Dispose();if(rawActivity!=null) rawActivity.Dispose();JabraManager.Default.Dispose();};
            hotplugTimer.Interval=TimeSpan.FromMilliseconds(650);hotplugTimer.Tick+=async delegate {hotplugTimer.Stop();await CheckTopology();};
            activityTimer.Interval=TimeSpan.FromMilliseconds(250);activityTimer.Tick+=delegate {foreach(var row in rows) row.NotifyTelemetry();if(busy) bottom.Text=operationPhase+"  \u00B7  "+(int)(DateTime.UtcNow-phaseStarted).TotalSeconds+"s";};activityTimer.Start();
        }

        T Find<T>(string name) where T:class {return (T)window.FindName(name);}
        static Button FindButton(DependencyObject child) {while(child!=null) {var button=child as Button;if(button!=null) return button;child=VisualTreeHelper.GetParent(child);}return null;}
        public void Show() {window.Show();}
        public void RunSelfTestLayout() {
            selfTestMode=true;
            rows.Add(new DeviceSlot {Number=1,Kind=10,Id="70:BF:92:11:22:33",Model="Jabra Evolve2 65",Brand="Jabra",SupportsConnect=true,IsConnected=true,IsLocked=true,OwnerLabel="Test"});
            candidates.Add(new Candidate {Kind=10,Id="70:BF:92:44:55:66",Model="Jabra Evolve2 75",Brand="Jabra",IsJabra=true});
            window.ShowInTaskbar=false;window.WindowStyle=WindowStyle.None;window.Opacity=0;window.Left=-10000;window.Top=-10000;
            window.Show();
            window.UpdateLayout();
            window.Close();
        }

        void ApplyTheme(bool dark,bool save) {
            Theme.Apply(window,dark,save);themeButton.Content=dark?"Light mode":"Dark mode";
            using(Stream logo=Assembly.GetExecutingAssembly().GetManifestResourceStream(dark?"DongleDeck.LogoDark.png":"DongleDeck.LogoLight.png")) {
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=logo;image.EndInit();Find<Image>("LogoImage").Source=image;
            }
            foreach(var row in rows) row.NotifyTelemetry();
        }

        void SetPhase(string status) {if(operationPhase!=status) {operationPhase=status;phaseStarted=DateTime.UtcNow;}bottom.Text=status;}
        void SetBusy(bool value,string status) {busy=value;SetPhase(status);operationProgress.Visibility=value?Visibility.Visible:Visibility.Collapsed;operationProgress.IsIndeterminate=value;RenderControls();if(!value && closeAfter) window.Close();else if(!value && topologyPending) {hotplugTimer.Stop();hotplugTimer.Start();}}

        void OnHardwareChanged(string path,bool arrived) {
            topologyPending=true;
            if(!arrived && current!=null && string.Equals(path,current.Receiver.ManagementPath,StringComparison.OrdinalIgnoreCase)) {StopWatcher();if(cancellation!=null) cancellation.Cancel();}
            hotplugTimer.Stop();hotplugTimer.Start();
        }

        async Task CheckTopology() {
            if(busy || checkingTopology || closeAfter) return;
            topologyPending=false;checkingTopology=true;
            try {await Refresh(true);} catch(Exception ex) {AppLog.Raw("Receiver detection: "+ex.Message);}
            finally {checkingTopology=false;if(topologyPending && !busy && !closeAfter) hotplugTimer.Start();}
        }

        void RefreshReceiverDropdown() {
            updating=true;
            string selectedKey=current==null?null:current.Receiver.Key;
            receiverBox.ItemsSource=null;
            receiverBox.ItemsSource=snapshots.Select(s=>s.Receiver).ToList();
            current=snapshots.FirstOrDefault(s=>s.Receiver.Key==selectedKey)??snapshots.FirstOrDefault();
            receiverBox.SelectedItem=current==null?null:current.Receiver;
            updating=false;
        }

        void ToggleCurrentReceiverLock() {
            if(busy || current==null) return;
            var receiver=current.Receiver;
            if(receiver.IsLocked) {
                DeviceLockStore.Default.UnlockReceiver(receiver);
                DeviceLockStore.Default.ApplyTo(current);
                RefreshReceiverDropdown();Render();
                Notice("Unlocked receiver "+receiver.Label+".",false);
            } else {
                string owner=DeviceLockStore.Default.DefaultOwnerLabel;
                DeviceLockStore.Default.LockReceiver(receiver,owner);
                DeviceLockStore.Default.ApplyTo(current);
                RefreshReceiverDropdown();Render();
                Notice("Locked receiver "+receiver.Label+" ("+owner+"). Pairing and unpairing are now protected.",false);
            }
        }

        void ToggleDeviceLock(DeviceSlot slot) {
            if(busy || current==null || slot==null) return;
            if(slot.IsLocked) {
                DeviceLockStore.Default.UnlockDevice(slot);
                DeviceLockStore.Default.ApplyTo(current);
                RenderControls();
                Notice("Unlocked "+slot.Model+".",false);
            } else {
                string owner=DeviceLockStore.Default.DefaultOwnerLabel;
                DeviceLockStore.Default.LockDevice(slot,current.Receiver,owner);
                DeviceLockStore.Default.ApplyTo(current);
                RenderControls();
                Notice("Locked "+slot.Model+" ("+owner+"). It cannot be unpaired until unlocked.",false);
            }
        }

        void ToggleComboLockPanel() {
            if(comboClaimActive) {CloseComboLockPanel();return;}
            comboClaimActive=true;
            detectedComboReceiver=null;detectedComboKeyboard=null;detectedComboMouse=null;
            ownerLabelBox.Text=DeviceLockStore.Default.DefaultOwnerLabel;
            lockReceiverTooCheck.IsChecked=true;
            // Seed with current receiver's keyboard & mouse if it's a Dell or Logitech dongle with clear slots.
            if(current!=null && (current.Receiver.IsDell || current.Receiver.IsLogitech)) {
                detectedComboReceiver=current;
                var kbs=current.Slots.Where(s=>s.Kind==1).ToList();
                var mice=current.Slots.Where(s=>s.Kind==2).ToList();
                if(kbs.Count==1) detectedComboKeyboard=kbs[0];
                if(mice.Count==1) detectedComboMouse=mice[0];
            }
            UpdateComboDetectStatus();
            comboLockPanel.Visibility=Visibility.Visible;
        }

        void CloseComboLockPanel() {
            comboClaimActive=false;
            comboLockPanel.Visibility=Visibility.Collapsed;
        }

        void UpdateComboDetectStatus() {
            var found=new List<string>();
            if(detectedComboKeyboard!=null) found.Add("Keyboard: "+detectedComboKeyboard.Model);
            if(detectedComboMouse!=null) found.Add("Mouse: "+detectedComboMouse.Model);
            if(detectedComboReceiver!=null && found.Count>0) {
                comboDetectText.Text="Detected on "+detectedComboReceiver.Receiver.Label+" \u2014 "+string.Join("  \u00B7  ",found)+". Click Confirm to lock them as your workstation combo (or wiggle/type on another combo to switch).";
            } else {
                comboDetectText.Text="Move your workstation mouse and press any key on your keyboard to detect your personal combo automatically.";
            }
            confirmComboLockButton.IsEnabled=detectedComboReceiver!=null && (detectedComboKeyboard!=null || detectedComboMouse!=null || lockReceiverTooCheck.IsChecked==true);
        }

        void ConfirmComboLock() {
            var targetReceiver=detectedComboReceiver ?? current;
            if(targetReceiver==null) {Notice("No receiver detected to lock.",true);return;}
            string owner=DeviceLockStore.NormalizeOwner(ownerLabelBox.Text);
            var devicesToLock=new List<DeviceSlot>();
            if(detectedComboKeyboard!=null) devicesToLock.Add(detectedComboKeyboard);
            if(detectedComboMouse!=null) devicesToLock.Add(detectedComboMouse);
            if(devicesToLock.Count==0) {
                devicesToLock.AddRange(targetReceiver.Slots.Where(s=>s.Kind==1 || s.Kind==2));
            }
            bool lockReceiver=lockReceiverTooCheck.IsChecked==true;
            int lockedCount=DeviceLockStore.Default.LockCombo(targetReceiver.Receiver,devicesToLock,owner,lockReceiver);
            foreach(var snap in snapshots) DeviceLockStore.Default.ApplyTo(snap);
            CloseComboLockPanel();
            RefreshReceiverDropdown();
            Render();
            Notice("Locked "+lockedCount+" device(s)"+(lockReceiver?" and workstation receiver":"")+" for "+owner+".",false);
        }

        void RenderControls() {
            refresh.IsEnabled=!busy;receiverBox.IsEnabled=!busy;lockComboButton.IsEnabled=!busy;
            bool available=current!=null && !busy;
            bool receiverLocked=current!=null && current.Receiver.IsLocked;
            bool hasCapacity=current!=null && (!current.Receiver.MaxSlots.HasValue || current.Slots.Count<current.Receiver.MaxSlots.Value);

            lockReceiverButton.IsEnabled=available;
            lockReceiverButton.Content=receiverLocked?"\uD83D\uDD13 Unlock receiver":"\uD83D\uDD12 Lock receiver";
            receiverLockBanner.Visibility=receiverLocked?Visibility.Visible:Visibility.Collapsed;
            if(receiverLocked) {
                receiverLockBannerText.Text="\uD83D\uDD12 Workstation receiver locked ("+current.Receiver.DisplayOwner+") \u2014 Pairing, removing, and clearing are blocked until unlocked.";
            }

            pairButton.IsEnabled=listening || (available && !receiverLocked && hasCapacity);
            pairText.Text=listening?"Stop pairing":"Start pairing";pairGlyph.Text=listening?"\u25A0":"\u25B6";
            System.Windows.Automation.AutomationProperties.SetName(pairButton,pairText.Text);
            pairButton.Background=pairButton.BorderBrush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(listening?"#B33D4D":"#237446"));
            scanState.Text=listening?"Scanning \u00B7 click Stop pairing when you\u2019re done":(receiverLocked?"Receiver is locked":"Scanning is off");
            scanState.SetResourceReference(TextBlock.ForegroundProperty,listening?"Success":"Muted");
            if(scanAnimated!=listening) {
                scanAnimated=listening;int index=0;
                foreach(string name in new[]{"WaveNear","WaveMid","WaveFar"}) {
                    var wave=Find<System.Windows.Shapes.Path>(name);
                    if(listening) wave.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0.15,1,new Duration(TimeSpan.FromSeconds(0.65))) {AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,BeginTime=TimeSpan.FromSeconds(index++*0.2)});
                    else {wave.BeginAnimation(UIElement.OpacityProperty,null);wave.Opacity=0.25;}
                }
            }

            int unlockedCount=current==null?0:current.Slots.Count(s=>!s.IsLocked);
            int lockedCount=current==null?0:current.Slots.Count(s=>s.IsLocked);
            bool supportsClear=current!=null && !current.Receiver.IsJabraDect;
            clear.IsEnabled=available && supportsClear && !receiverLocked && unlockedCount>0;
            clear.Content=lockedCount>0 && unlockedCount>0?("Clear "+unlockedCount+" unlocked"):(lockedCount>0 && unlockedCount==0?"All locked":"Clear pairings");
            deviceList.IsEnabled=!busy;

            bool supportsAuto=current!=null && current.Receiver.SupportsAutoPair;
            automatic.Visibility=supportsAuto?Visibility.Visible:Visibility.Collapsed;
            if(current!=null && current.Receiver.IsLogitech) automatic.IsChecked=true;
            else if(current!=null && (current.Receiver.IsWindowsBluetooth || current.Receiver.IsJabraDect)) automatic.IsChecked=false;

            jabraOnlyCheck.Visibility=(current!=null && current.Receiver.IsWindowsBluetooth)?Visibility.Visible:Visibility.Collapsed;
            jabraOnlyCheck.IsEnabled=!busy;
        }

        void Render() {
            rows.Clear();
            if(current!=null) foreach(var slot in current.Slots.OrderBy(s=>s.Number)) rows.Add(slot);
            emptyPanel.Visibility=rows.Count>0?Visibility.Collapsed:Visibility.Visible;
            if(current==null) {
                meta.Text="Plug in a Dell Universal, Logitech Unifying, or Jabra Link receiver, or enable Windows Bluetooth.";
                capacity.Text="";
                pairedSubtitle.Text="Switch a device off and on to identify its exact row.";
                emptySubtitle.Text="Add a keyboard, mouse, or Jabra headset to get this receiver ready.";
            } else {
                var r=current.Receiver;
                meta.Text=(r.IsWindowsBluetooth?"Bluetooth Radio ":"USB ")+r.UsbId+"  \u00B7  "+r.Revision;
                capacity.Text=r.MaxSlots.HasValue?(current.Slots.Count+" of "+r.MaxSlots.Value+" slots used"):(current.Slots.Count+" paired audio device"+(current.Slots.Count==1?"":"s"));
                pairedSubtitle.Text=r.IsJabra || r.IsWindowsBluetooth?"Pair, connect, disconnect, lock, or remove headsets and earbuds.":"Switch a device off and on to identify its exact row. Lock your workstation combo to protect it.";
                emptySubtitle.Text=r.IsJabraDect?"Click Start pairing and plug the Jabra Engage DECT headset in via USB cable.":(r.IsJabra || r.IsWindowsBluetooth?"Put your Jabra headset or earbuds in pairing mode and click Start pairing.":"Add a keyboard and mouse to get this receiver ready.");
            }
            RenderControls();
        }

        void Notice(string message,bool error) {
            notice.Text=message;notice.SetResourceReference(TextBlock.ForegroundProperty,error?"Danger":"Success");
            noticeBorder.SetResourceReference(Border.BackgroundProperty,error?"DangerSoft":"SuccessSoft");AppLog.Raw(message);
        }

        void UpdateAutomatic() {
            bool auto=automatic.IsChecked==true;
            if(pairing!=null) pairing.Automatic=auto;
            var kind=current==null?ReceiverKind.DellUniversal:current.Receiver.Kind;
            bool showCandidates=(!auto || kind==ReceiverKind.WindowsBluetooth || kind==ReceiverKind.JabraBluetoothDongle) && kind!=ReceiverKind.JabraDectDongle;
            candidateList.Visibility=showCandidates?Visibility.Visible:Visibility.Collapsed;
            instructions.Text=JabraClassifier.PairingInstructions(kind,auto);
            pairPanelSubtitle.Text=current!=null && current.Receiver.MaxSlots.HasValue
                ?("Keeps scanning until you click Stop pairing, or all "+current.Receiver.MaxSlots.Value+" slots are full.")
                :"Keeps scanning until you click Stop pairing.";
        }

        void RenderServiceStatus(ReceiverInventory inventory) {
            var status=inventory.DellService;
            bool dellError=status.BlocksDell || status.State==DellServiceState.Unknown;
            bool hasJabraWarning=inventory.JabraDirect!=null && inventory.JabraDirect.Running;
            bool hasJabraError=!string.IsNullOrEmpty(inventory.JabraStartupError);

            bool error=dellError || hasJabraError;
            ddpmBorder.SetResourceReference(Border.BackgroundProperty,error?"DangerSoft":(hasJabraWarning?"LockSoft":"SuccessSoft"));
            ddpmTitle.SetResourceReference(TextBlock.ForegroundProperty,error?"Danger":(hasJabraWarning?"LockAccent":"Success"));
            ddpmGlyph.SetResourceReference(TextBlock.ForegroundProperty,error?"Danger":(hasJabraWarning?"LockAccent":"Success"));
            ddpmGlyph.Text=error?"!":(hasJabraWarning?"!":"\u2713");

            string title=status.Title;
            if(hasJabraWarning) title+="  \u00B7  Jabra Direct running";
            ddpmTitle.Text=title;

            var details=new List<string>();
            if(!string.IsNullOrEmpty(status.Detail)) details.Add(status.Detail);
            if(hasJabraWarning) details.Add(inventory.JabraDirect.Notice);
            if(hasJabraError) details.Add(inventory.JabraStartupError);
            ddpmDetail.Text=string.Join("\n",details);
            ddpmDetail.Visibility=details.Count==0?Visibility.Collapsed:Visibility.Visible;
        }

        async Task Refresh(bool fromHotplug) {
            if(busy) return;string key=current==null?null:current.Receiver.Key;SetBusy(true,"Scanning receivers & Bluetooth\u2026");StopWatcher();
            try {
                string autoSelectedName=await Reload(key,fromHotplug);
                if(!string.IsNullOrEmpty(autoSelectedName)) Notice("New unlocked receiver detected: "+autoSelectedName+" (auto-selected).",false);
                else Notice(current==null?"No accessible receiver or Bluetooth radio found.":"Ready.",false);
            } catch(Exception ex) {Notice(ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }

        async Task<string> Reload(string preferredKey,bool autoSelectNewUnlocked) {
            bool jabraOnly=jabraOnlyCheck.IsChecked==true;
            var inventory=await ReceiverOperations.InspectAsync(jabraOnly);
            var results=inventory.Receivers;
            RenderServiceStatus(inventory);

            foreach(var snapshot in results) foreach(var slot in snapshot.Slots) {
                string id=snapshot.Receiver.Key+"/"+slot.Identity;DeviceSlot old;
                if(telemetry.TryGetValue(id,out old)) slot.CopyTelemetry(old);telemetry[id]=slot;
            }

            string autoSelected=null;
            if(autoSelectNewUnlocked && knownReceiverKeys.Count>0) {
                var newlyAdded=results.FirstOrDefault(s=>!knownReceiverKeys.Contains(s.Receiver.Key) && !s.Receiver.IsLocked && !s.Receiver.IsWindowsBluetooth);
                bool wasLockedOrMissing=current==null || current.Receiver.IsLocked || current.Receiver.IsWindowsBluetooth;
                if(newlyAdded!=null && wasLockedOrMissing) {
                    preferredKey=newlyAdded.Receiver.Key;
                    autoSelected=newlyAdded.Receiver.Label;
                }
            }
            knownReceiverKeys.Clear();
            foreach(var s in results) knownReceiverKeys.Add(s.Receiver.Key);

            snapshots=results;updating=true;receiverBox.ItemsSource=snapshots.Select(s=>s.Receiver).ToList();
            current=snapshots.FirstOrDefault(s=>s.Receiver.Key==preferredKey)??snapshots.FirstOrDefault(s=>!s.Receiver.IsLocked)??snapshots.FirstOrDefault();
            receiverBox.SelectedItem=current==null?null:current.Receiver;updating=false;Render();
            return autoSelected;
        }

        void StopWatcher() {if(watcher!=null) {watcher.Dispose();watcher=null;}}
        void StartWatcher() {
            StopWatcher();if(current==null || listening || closeAfter || current.Receiver.IsJabra || current.Receiver.IsWindowsBluetooth) return;
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
            if((DateTime.UtcNow-lastInputUi[kind]).TotalMilliseconds<180) return;
            lastInputUi[kind]=DateTime.UtcNow;

            var matchedSnap=snapshots.FirstOrDefault(s=>s.Receiver.PhysicalDeviceId!=null && s.Receiver.PhysicalDeviceId==physical);
            if(matchedSnap==null) return;

            var matching=matchedSnap.Slots.Where(s=>s.Kind==kind).ToList();
            var active=matching.Where(s=>s.ReportedOnline==true).ToList();
            DeviceSlot exact=matching.Count==1?matching[0]:(active.Count==1 && matching.All(s=>s==active[0] || s.ReportedOnline==false)?active[0]:null);

            if(comboClaimActive) {
                detectedComboReceiver=matchedSnap;
                if(exact!=null) {
                    if(kind==1) detectedComboKeyboard=exact;
                    else if(kind==2) detectedComboMouse=exact;
                }
                UpdateComboDetectStatus();
            }

            if(current==null || current.Receiver.Key!=matchedSnap.Receiver.Key) return;
            if(exact!=null) {exact.ReportedOnline=true;exact.LastSeenUtc=DateTime.UtcNow;exact.LastInputUtc=DateTime.UtcNow;exact.NotifyTelemetry();activity.Text="";}
            else activity.Text=(kind==1?"Keyboard":"Mouse")+" activity on this receiver. Switch one device off/on to identify its exact row.";
        }

        async Task Pair() {
            if(busy || current==null) return;var receiver=current.Receiver;
            try {DeviceLockStore.Default.EnsureCanPair(receiver);} catch(Exception ex) {Notice(ex.Message,true);return;}
            StopWatcher();listening=true;pairPanel.Visibility=Visibility.Visible;UpdateAutomatic();cancellation=new CancellationTokenSource();
            SetBusy(true,"Pairing mode on");
            try {
                while(!cancellation.IsCancellationRequested && current!=null && current.Receiver.Key==receiver.Key && (!receiver.MaxSlots.HasValue || current.Slots.Count<receiver.MaxSlots.Value)) {
                    candidates.Clear();candidateList.IsEnabled=true;finishing=false;searchStatus.Text="Opening receiver\u2026";SetPhase(searchStatus.Text);countdown.Text="";
                    pairing=new PairingSession {Automatic=automatic.IsChecked==true && receiver.SupportsAutoPair,JabraOnly=jabraOnlyCheck.IsChecked==true};
                    var progress=new Progress<PairProgress>(p=> {
                        if(p.Candidate!=null && !candidates.Any(c=>c.Id==p.Candidate.Id)) candidates.Add(p.Candidate);
                        searchStatus.Text=p.Selecting?p.Message:((pairing.Automatic && receiver.IsDell)?"Waiting for a keyboard or mouse\u2026":p.Message);
                        SetPhase(searchStatus.Text);
                        countdown.Text=p.Selecting?"Saving pairing\u2026":"Continuous scan";
                        if(p.Selecting) {finishing=true;candidateList.IsEnabled=false;}
                    });
                    DeviceSlot added=await Task.Run(()=>pairing.Run(receiver,0,progress,cancellation.Token));
                    if(added!=null) {
                        Notice(added.Model+" paired.",false);SetPhase("Updating devices\u2026");
                        await Reload(receiver.Key,false);
                        var row=current==null?null:current.Slots.FirstOrDefault(s=>s.Id==added.Id && s.Kind==added.Kind);
                        if(row!=null) {row.ReportedOnline=true;row.LastSeenUtc=DateTime.UtcNow;row.NotifyTelemetry();}
                        if(receiver.IsJabraDect) break;
                    }
                }
                if(current!=null && receiver.MaxSlots.HasValue && current.Slots.Count>=receiver.MaxSlots.Value) {
                    Notice("All "+receiver.MaxSlots.Value+" slots are used. Remove a device to make room.",false);
                }
            } catch(OperationCanceledException) {Notice("Pairing mode stopped.",false);} catch(Exception ex) {Notice(ex.Message,true);}
            finally {pairPanel.Visibility=Visibility.Collapsed;pairing=null;if(cancellation!=null) {cancellation.Dispose();cancellation=null;}finishing=false;listening=false;}
            try {await Reload(receiver.Key,false);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }

        async Task ToggleConnect(DeviceSlot slot) {
            if(busy || current==null) return;var receiver=current.Receiver;StopWatcher();SetBusy(true,(slot.IsConnected?"Disconnecting ":"Connecting ")+slot.Model+"\u2026");
            try {
                string msg=await ReceiverOperations.ToggleConnectAsync(receiver,slot);
                Notice(msg,false);
            } catch(Exception ex) {Notice(ex.Message,true);}
            try {await Reload(receiver.Key,false);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }

        async Task Remove(DeviceSlot slot) {
            if(busy || current==null) return;var receiver=current.Receiver;StopWatcher();SetBusy(true,"Removing "+slot.Model+"\u2026");
            try {
                var updated=await ReceiverOperations.RemoveAsync(receiver,slot);
                if(updated!=null) {current.Slots=updated;DeviceLockStore.Default.ApplyTo(current);Render();}
                Notice(slot.Model+" removed.",false);
            } catch(Exception ex) {Notice(ex.Message,true);}
            try {await Reload(receiver.Key,false);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }

        async Task Clear() {
            if(busy || current==null) return;var receiver=current.Receiver;var targets=current.Slots.ToList();StopWatcher();SetBusy(true,"Clearing unlocked pairings\u2026");
            try {
                int lockedKept=targets.Count(s=>s.IsLocked);
                var updated=await ReceiverOperations.ClearAsync(receiver,targets);
                if(updated!=null) {current.Slots=updated;DeviceLockStore.Default.ApplyTo(current);Render();}
                Notice(lockedKept>0?("Cleared unlocked pairings ("+lockedKept+" locked device"+(lockedKept==1?"":"s")+" preserved)."):"Pairings cleared.",false);
            } catch(Exception ex) {Notice(ex.Message,true);}
            try {await Reload(receiver.Key,false);} catch(Exception ex) {Notice("Refresh needed: "+ex.Message,true);} finally {SetBusy(false,"Ready");StartWatcher();}
        }
    }

    public static class Program {
        [STAThread] public static int Main(string[] args) {
            bool selfTest=args!=null && args.Any(a=>string.Equals(a,"--self-test",StringComparison.OrdinalIgnoreCase));
            AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e) {
                string msg=e.ExceptionObject!=null?e.ExceptionObject.ToString():"Unknown fatal error";
                AppLog.Raw("FATAL UnhandledException: "+msg);
                if(selfTest) Console.Error.WriteLine(msg);
                else try {MessageBox.Show(msg,"DongleDeck Prime - Startup Error",MessageBoxButton.OK,MessageBoxImage.Error);} catch {}
            };
            TaskScheduler.UnobservedTaskException+=delegate(object sender,UnobservedTaskExceptionEventArgs e) {
                AppLog.Raw("UnobservedTaskException: "+e.Exception);
                e.SetObserved();
            };
            try {
                AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges",false);
                AppLog.Raw("DongleDeck Prime 0.6.0 starting \u00B7 admin="+DellServiceStatus.IsAdministrator+" \u00B7 baseDir="+AppContext.BaseDirectory);
                bool created;using(var mutex=new Mutex(true,"Local\\DongleDeck.Utility",out created)) {
                    if(!created && !selfTest) {MessageBox.Show("DongleDeck Prime is already open.");return 1;}
                    var app=new Application();
                    app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e) {
                        AppLog.Raw("DispatcherUnhandledException: "+e.Exception);
                        if(!selfTest) MessageBox.Show(e.Exception.Message,"DongleDeck Prime");
                        e.Handled=true;
                    };
                    var main=new MainWindow();
                    AppLog.Raw("MainWindow initialized successfully");
                    if(selfTest) {
                        main.RunSelfTestLayout();
                        AppLog.Raw("MainWindow layout & templates verified");
                        Console.WriteLine("SELF-TEST OK");
                        return 0;
                    }
                    main.Show();
                    return app.Run();
                }
            } catch(Exception ex) {
                AppLog.Raw("FATAL Main: "+ex);
                if(selfTest) {
                    Console.Error.WriteLine("SELF-TEST FAILED: "+ex);
                    return 1;
                }
                try {MessageBox.Show("DongleDeck Prime failed to start:\n\n"+ex.Message+"\n\nDetails were written to:\n"+AppLog.PathName,"DongleDeck Prime - Startup Error",MessageBoxButton.OK,MessageBoxImage.Error);} catch {}
                return 1;
            }
        }
    }
}
