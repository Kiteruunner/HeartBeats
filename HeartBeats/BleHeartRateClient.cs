using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace HeartBeats;

public sealed class BleHeartRateClient
{
    private static readonly Guid HrService = Guid.Parse("0000180D-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HrMeasurement = Guid.Parse("00002A37-0000-1000-8000-00805F9B34FB");

    private BluetoothLEDevice? _dev;
    private GattCharacteristic? _hrChar;

    public event Action<int>? OnBpm;
    public event Action<string>? OnStatus;

    public async Task ConnectAndSubscribeAsync(string mac)
    {
        OnStatus?.Invoke("CONNECTING");

        ulong address = ParseMacToUlong(mac);
        _dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

        if (_dev == null) { OnStatus?.Invoke("DEVICE NULL"); return; }

        var svcResult = await _dev.GetGattServicesForUuidAsync(HrService, BluetoothCacheMode.Uncached);
        var svc = svcResult.Services.FirstOrDefault();
        if (svc == null) { OnStatus?.Invoke("NO HR SERVICE"); return; }

        var chResult = await svc.GetCharacteristicsForUuidAsync(HrMeasurement, BluetoothCacheMode.Uncached);
        _hrChar = chResult.Characteristics.FirstOrDefault();
        if (_hrChar == null) { OnStatus?.Invoke("NO HR CHAR"); return; }

        _hrChar.ValueChanged += HrCharOnValueChanged;

        var status = await _hrChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

        OnStatus?.Invoke(status == GattCommunicationStatus.Success ? "LIVE" : $"SUB FAIL:{status}");
    }

    public async Task DisconnectAsync()
    {
        if (_hrChar != null)
        {
            _hrChar.ValueChanged -= HrCharOnValueChanged;
            try
            {
                await _hrChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
        }

        _hrChar = null;
        _dev?.Dispose();
        _dev = null;
        OnStatus?.Invoke("DISCONNECTED");
    }

    private void HrCharOnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = new byte[args.CharacteristicValue.Length];
        DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(data);

        int bpm = ParseBpm(data);
        if (bpm > 0) OnBpm?.Invoke(bpm);
    }

    private static int ParseBpm(byte[] data)
    {
        if (data == null || data.Length < 2) return -1;

        int flags = data[0] & 0xFF;
        bool is16 = (flags & 0x01) != 0;

        if (!is16) return data[1] & 0xFF;
        if (data.Length < 3) return -1;

        return (data[1] & 0xFF) | ((data[2] & 0xFF) << 8);
    }

    private static ulong ParseMacToUlong(string mac)
    {
        string hex = mac.Replace(":", "").Replace("-", "").Trim();
        return Convert.ToUInt64(hex, 16);
    }
}
