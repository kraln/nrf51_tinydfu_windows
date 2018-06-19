using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Core;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using System.Threading.Tasks;
using Windows.Storage;
using System.Diagnostics;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Bootloader
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    /// 
    public sealed partial class MainPage : Page
    {

        private ObservableCollection<BLEDevice> KnownDevices = new ObservableCollection<BLEDevice>();
        private DeviceWatcher deviceWatcher;
        GattCharacteristic write;
        GattCharacteristic read;

        public Windows.Storage.StorageFile file;

        /// <summary>
        /// Helper class
        /// </summary>
        public class BLEDevice
        {
            public string Name;
            public string id;
            public DeviceInformation di;
            public BLEDevice(DeviceInformation d)
            {
                di = d;
                id = d.Id;
                Name = d.Name;
            }
            public BLEDevice(DeviceInformationUpdate diu)
            {
                di = null;
                id = diu.Id;
                Name = "";
            }
            public override bool Equals(Object obj)
            {
                if (obj == null || GetType() != obj.GetType())
                    return false;

                return ((BLEDevice)obj).id == id;
            }
            public override int GetHashCode()
            {
                return id.GetHashCode() ^ di.GetHashCode();
            }
        }

        /// <summary>
        /// Entrypoint
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
            log("To begin, scan for bluetooth devices");
        }

        /// <summary>
        /// Add a line to the log
        /// </summary>
        /// <param name="s">What to log</param>
        void log(String s)
        {
            Output.Text = Output.Text + "\r\n" + s;
            Scroll.ChangeView(null, Scroll.ExtentHeight, null, true);
        }

        #region handlers
        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (deviceWatcher == null)
            {
                ((MenuFlyout)this.Resources["DeviceList"]).Items.Clear();
                Devices.IsEnabled = false;
                StartBleDeviceWatcher();
                Scan.IsChecked = true;
                log("Scanning enabled");
            }
            else
            {
                StopBleDeviceWatcher();
                Scan.IsChecked = false;
                log("Scanning disabled");
            }

        }

        private async void Select_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".bin");

            file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                log("Picked firmware: " + file.Name);
                StatusUpdate.Text = "Firmware: " + file.Name;
                Upload.IsEnabled = true;
            }
        }

        List<Byte[]> erasecommands;
        List<Byte[]> writecommands;

        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                uint curpage = 96;
                uint curportion = 0;

                writecommands = new List<byte[]>();
                erasecommands = new List<byte[]>();

                var dbytes = new byte[2];
                dbytes[0] = (byte)'d';
                dbytes[1] = (byte)curpage;
                erasecommands.Add(dbytes);

                // turn the file into a series of commands
                IBuffer inputstream = await FileIO.ReadBufferAsync(file);
                {
                    using (var dataReader = DataReader.FromBuffer(inputstream))
                    {
                        while (dataReader.UnconsumedBufferLength > 16)
                        {
                            var bytes = new byte[16]; // bytes in one command
                            dataReader.ReadBytes(bytes);

                            var cmdbytes = new byte[19];
                            cmdbytes[0] = (byte)'w';
                            cmdbytes[1] = (byte)curpage;
                            cmdbytes[2] = (byte)curportion;
                            bytes.CopyTo(cmdbytes, 3);

                            writecommands.Add(cmdbytes);

                            /* wraps around 64 */
                            curportion++;
                            if (curportion > 63)
                            {
                                curportion = 0;
                                curpage++;
                                var d_bytes = new byte[2];
                                d_bytes[0] = (byte)'d';
                                d_bytes[1] = (byte)curpage;
                                erasecommands.Add(d_bytes);
                            }

                            if (curpage > 239)
                            {
                                log("Load aborted... file too large!");
                                return;
                            }

                        }

                        if (dataReader.UnconsumedBufferLength > 0)
                        {
                            var bytes = new byte[dataReader.UnconsumedBufferLength]; // bytes in one command
                            dataReader.ReadBytes(bytes);

                            var cmdbytes = new byte[19];
                            cmdbytes[0] = (byte)'w';
                            cmdbytes[1] = (byte)curpage;
                            cmdbytes[2] = (byte)curportion;
                            bytes.CopyTo(cmdbytes, 3);

                            writecommands.Add(cmdbytes);

                            /* wraps around 64 */
                            curportion++;
                            if (curportion > 63)
                            {
                                curportion = 0;
                                curpage++;
                                var d_bytes = new byte[2];
                                d_bytes[0] = (byte)'d';
                                d_bytes[1] = (byte)curpage;
                                erasecommands.Add(d_bytes);
                            }

                            if (curpage > 239)
                            {
                                log("Load aborted... file too large!");
                                return;
                            }
                        }

                    }
                }

                // send each command, wait for ack

                foreach (var cmd in erasecommands)
                {
                    matched = false;
                    expected_res = new byte[2];
                    expected_res[0] = cmd[0];
                    expected_res[1] = cmd[1];

                    var writer = new DataWriter();
                    writer.WriteBytes(expected_res);

                    var res = await write.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                    if (res != GattCommunicationStatus.Success)
                    {
                        log("Write Error, aborting!");
                        return;
                    }

                    uint countdown = 255;
                    while (!matched && countdown-- > 0)
                    {
                        await Task.Delay(1);
                    }

                    if (countdown == 0)
                    {
                        log("Timedout waiting for response!");
                        return;
                    }
                }

                uint cmdcount = 0;
                foreach (var cmd in writecommands)
                {
                    cmdcount++;
                    matched = false;
                    expected_res = new byte[2];
                    expected_res[0] = cmd[0];
                    expected_res[1] = cmd[1];

                    var writer = new DataWriter();
                    writer.WriteBytes(cmd);

                    var res = await write.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                    if (res != GattCommunicationStatus.Success)
                    {
                        log("Write Error, aborting!");
                        return;
                    }

                    uint countdown = 255;
                    while (!matched && countdown-- > 0)
                    {
                        await Task.Delay(1);
                    }

                    if (countdown == 0)
                    {
                        log("Timedout waiting for response!");
                        return;
                    }

                    StatusUpdate.Text = "Updating firmware: " + (((float)cmdcount / (float)writecommands.Count) * 100f).ToString("0.00") + "%";

                }
                sw.Stop();
                log("Complete! Time: " + sw.Elapsed.TotalSeconds + "s");
                StatusUpdate.Text = "Complete!";
            });
        }

        private void Device_Click(object sender, RoutedEventArgs e)
        {
            FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
        }
        #endregion

        #region Device discovery

        private void StartBleDeviceWatcher()
        {
            string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.Bluetooth.Le.IsConnectable", "System.ItemNameDisplay", "System.Devices.Aep.SignalStrength" };
            /* paired and unpaired */
            string aqsAllBluetoothLEDevices = "(System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";

            deviceWatcher =
                    DeviceInformation.CreateWatcher(
                        aqsAllBluetoothLEDevices,
                        requestedProperties,
                        DeviceInformationKind.AssociationEndpoint);

            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Updated += DeviceWatcher_Updated;
            deviceWatcher.Removed += DeviceWatcher_Removed;
            deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
            deviceWatcher.Stopped += DeviceWatcher_Stopped;

            KnownDevices.Clear();
            deviceWatcher.Start();
        }

        private void StopBleDeviceWatcher()
        {
            if (deviceWatcher != null)
            {
                deviceWatcher.Added -= DeviceWatcher_Added;
                deviceWatcher.Updated -= DeviceWatcher_Updated;
                deviceWatcher.Removed -= DeviceWatcher_Removed;
                deviceWatcher.EnumerationCompleted -= DeviceWatcher_EnumerationCompleted;
                deviceWatcher.Stopped -= DeviceWatcher_Stopped;

                deviceWatcher.Stop();
                deviceWatcher = null;
            }
        }

        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                    log(String.Format("Added {0}{1}", deviceInfo.Id, deviceInfo.Name));

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        BLEDevice ble = new BLEDevice(deviceInfo);
                        // Make sure device isn't already present in the list.
                        if (!KnownDevices.Contains(ble))
                        {
                            KnownDevices.Add(ble);
                        }

                    }
                }
            });
        }

        private async void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                    // log(String.Format("Updated {0}{1}", deviceInfoUpdate.Id, ""));

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        foreach (BLEDevice b in KnownDevices)
                        {
                            if (b.id == deviceInfoUpdate.Id)
                            {
                                b.di.Update(deviceInfoUpdate);
                                b.Name = b.di.Name;
                            }
                        }
                    }
                }
            });
        }

        private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                    log(String.Format("Removed {0}{1}", deviceInfoUpdate.Id, ""));

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        List<BLEDevice> deadlist = new List<BLEDevice>();

                        foreach (BLEDevice b in KnownDevices)
                        {
                            if (b.id == deviceInfoUpdate.Id)
                            {
                                deadlist.Add(b);
                            }
                        }

                        foreach (BLEDevice b in deadlist)
                        {
                            KnownDevices.Remove(b);
                        }
                    }
                }
            });
        }

        private async void ScanDevice(BLEDevice b)
        {
            StatusUpdate.Text = "Using device " + b.Name;
            log("Using device " + b.Name + ", " + b.id);
            log("Enumerating Services and Characteristics...");
            var dev = await BluetoothLEDevice.FromIdAsync(b.id);
            if (dev == null)
            {
                log("... Couldn't get Device from ID");
                return;
            }
            var services = await dev.GetGattServicesAsync();
            if (services == null || services.Services == null)
            {
                log("... Couldn't get services from device");
                return;
            }
            bool found = false;
            foreach (GattDeviceService gds in services.Services)
            {
                log(b.Name + ": s" + gds.Uuid);
                if (gds.Uuid.ToString().Equals("6e400001-b5a3-f393-e0a9-e50e24dcca9e")) // nordic serial 
                {
                    found = true;
                }
                var characts = await gds.GetCharacteristicsAsync();
                if (characts == null || characts.Characteristics == null)
                {
                    log(b.Name + ": s" + gds.Uuid + ", couldn't enumerate characteristics");
                    continue;
                }
                foreach (GattCharacteristic gc in characts.Characteristics)
                {
                    log(b.Name + ": s" + gds.Uuid + ", c" + gc.Uuid);
                    if (found && gc.Uuid.ToString().Equals("6e400003-b5a3-f393-e0a9-e50e24dcca9e")) // rx
                    {
                        read = gc;
                        var res = await read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                        if (res == GattCommunicationStatus.Success)
                        {
                            log("Registered for notifications");
                            read.ValueChanged += RX;
                        }

                    }

                    if (found && gc.Uuid.ToString().Equals("6e400002-b5a3-f393-e0a9-e50e24dcca9e")) // tx
                    {
                        write = gc;
                    }
                }
            }

            if (!found)
            {
                log("Compatable service not found");
                Open.IsEnabled = false;
                Upload.IsEnabled = false;
            }
            else
            {
                Open.IsEnabled = true;
            }

        }

        private async void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object e)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                if (sender == deviceWatcher)
                {
                    log(KnownDevices.Count + " devices found. Enumeration completed.");

                    Devices.IsEnabled = true;
                    ((MenuFlyout)this.Resources["DeviceList"]).Items.Clear();

                    foreach (BLEDevice b in KnownDevices)
                    {
                        MenuFlyoutItem mfi = new MenuFlyoutItem();
                        mfi.Text = b.Name;
                        mfi.Click += (o, i) =>
                        {
                            ScanDevice(b);
                        };

                        ((MenuFlyout)this.Resources["DeviceList"]).Items.Add(mfi);
                    }

                }
            });
        }

        private async void DeviceWatcher_Stopped(DeviceWatcher sender, object e)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                if (sender == deviceWatcher)
                {
                    log("No longer watching for new devices");
                }
            });
        }
        #endregion


        public byte[] expected_res;
        public bool matched = false;
        async void RX(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                byte[] read_data = new byte[args.CharacteristicValue.Length];
                reader.ReadBytes(read_data);

                if (expected_res != null && expected_res[0] == read_data[0] && expected_res[1] == read_data[1])
                {
                    matched = true;
                }
                else
                {
                    matched = false;
                    log("Read: " + BitConverter.ToString(read_data) + ", " + System.Text.Encoding.UTF8.GetString(read_data));
                }
            });
        }
    }
}
