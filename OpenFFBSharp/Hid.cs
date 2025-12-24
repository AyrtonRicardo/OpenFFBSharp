using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Device.Net;
using Hid.Net;
using Hid.Net.Windows;
using static OpenFFBoard.Commands;

namespace OpenFFBoard
{
    public class Hid : Board
    {
        private static IHidDevice _board;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private const int Timeout = 500;

        public Hid(IHidDevice device)
        {
            _board = device;
        }

        public IHidDevice GetBoard()
        {
            return _board;
        }

        /// <summary>
        /// Fetch currently connected OpenFFBoards
        /// </summary>
        /// <returns></returns>
        public static async Task<IHidDevice[]> GetBoardsAsync()
        {
            var hidFactory =
                new FilterDeviceDefinition(0x1209, 0xFFB0, label: "OpenFFBoard")
                    .CreateWindowsHidDeviceFactory();

            var deviceDefinitions =
                (await hidFactory.GetConnectedDeviceDefinitionsAsync().ConfigureAwait(false)).ToArray();
            IHidDevice[] devices = new IHidDevice[deviceDefinitions.Length];

            for (int i = 0; i < deviceDefinitions.Length; i++)
                devices[i] = (IHidDevice)await hidFactory.GetDeviceAsync(deviceDefinitions[i]).ConfigureAwait(false);

            return devices;
        }

        /// <summary>
        /// Send command to OpenFFBoard (and receive data)
        /// </summary>
        /// <param name="device">Device to send data to</param>
        /// <param name="boardClass">Target class. Used for directing commands at a specific class.</param>
        /// <param name="instance">Instance of the targeted class. Used for directing commands at a specific driver/axis.</param>
        /// <param name="cmd">Command to send (board parameters)</param>
        /// <param name="data">Data to send</param>
        /// <param name="address">Second data to send (optional)</param>
        /// <returns></returns>
        public async Task<Commands.BoardResponse<T>> SendCmdAsync<T>(IHidDevice device, BoardClass boardClass, byte? instance, BoardCommand cmd, T data, ulong? address)
        {
            await _semaphore.WaitAsync();
            byte[] response;
            await WriteLineAsync(device, ConstructMessage<T>(boardClass, instance, cmd, address, data));

            var task = ReadCommandAsync(device);
            if (await Task.WhenAny(task, Task.Delay(Timeout)) == task)
            {
                await task;
                response = task.Result;
            }
            else
            {
                _semaphore.Release();
                return null;
            }
            _semaphore.Release();

            return Task.Run(() => ParseBoardResponse<T>(cmd, response)).Result;
        }

        private Commands.BoardResponse<T> ParseBoardResponse<T>(BoardCommand cmd, byte[] data)
        {
            T _data;
            if (typeof(T) == typeof(byte[]))
            {
                _data = default;
            }
            else
            {
                _data = (T)Convert.ChangeType(BitConverter.ToInt64(data, 9), typeof(T));
            }

            return new Commands.BoardResponse<T>
            {
                Type = (Commands.CmdType)data[1],
                ClassId = BitConverter.ToUInt16(data, 2),
                Instance = data[4],
                Cmd = cmd,
                Data = _data,
                Address = BitConverter.ToUInt64(data, 17)
            };
        }

        private async Task<byte[]> ReadCommandAsync(IHidDevice device)
        {
            TransferResult readBuffer;
            do
            {
                readBuffer = await device.ReadAsync().ConfigureAwait(false);
            } while (readBuffer.Data[0] != 0xA1);
            return readBuffer.Data;
        }

        private byte[] ConstructMessage<T>(BoardClass classId, byte? instance, BoardCommand cmd, ulong? address, T data)
        {
            CmdType type;
            if (address != null && (data == null || data.Equals(default(T))))
                type = CmdType.RequestAddress;
            else if (address == null && (data == null || data.Equals(default(T))))
                type = CmdType.Request;
            else if (address == null)
                type = CmdType.Write;
            else
                type = CmdType.WriteAddress;

            long hidData;
            if (data == null)
                hidData = 0;
            else if (typeof(T) == typeof(byte[]))
            {
                var bytes = (byte[])Convert.ChangeType(data, typeof(byte[]));
                hidData = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    hidData |= (long)bytes[i] << (i * 8);
                }
            }
            else
                hidData = (long)Convert.ChangeType(data, typeof(long));

            var buffer = new byte[25];
            buffer[0] = 0xA1;
            buffer[1] = (byte)type; //type
            BitConverter.GetBytes(classId.ClassId).CopyTo(buffer, 2); //classID
            buffer[4] = instance ?? 0; //instance
            BitConverter.GetBytes(cmd.Id).CopyTo(buffer, 5); //cmd
            BitConverter.GetBytes(hidData).CopyTo(buffer, 9); //data 1
            BitConverter.GetBytes(address ?? 0).CopyTo(buffer, 17); //data 2 (address)
            return buffer;
        }

        /// <summary>
        /// Write a report to the HiDDevice asynchronously
        /// </summary>
        /// <param name="device">The device to send data to</param>
        /// <param name="buffer">The data to send</param>
        /// <returns></returns>
        public async Task WriteLineAsync(IHidDevice device, byte[] buffer)
        {
            await device.WriteAsync(buffer).ConfigureAwait(false);
        }

        public override async Task ConnectAsync()
        {
            if (_board == null)
                throw new NullReferenceException(
                    "OpenFFBoard not assigned, please use the SetBoard function to assign a board.");

            try
            {
                await _board.InitializeAsync().ConfigureAwait(false);
                IsConnected = _board.IsInitialized;
            }
            catch
            {
                IsConnected = false;
                throw;
            }
        }

        public override void Connect()
        {
            if (_board != null)
            {
                ConnectAsync().GetAwaiter().GetResult();
            }
            else
            {
                IsConnected = false;
                throw new NullReferenceException(
                    "OpenFFBoard not assigned, please use the SetBoard function to assign a board.");
            }
        }

        public override void Disconnect()
        {
            if (_board != null)
                _board.Close();
            else
            {
                throw new NullReferenceException(
                    "OpenFFBoard not assigned, please use the SetBoard function to assign a board.");
            }
        }

        internal override Commands.BoardResponse<T> GetBoardData<T>(BoardClass boardClass, byte? instance, BoardCommand<T> cmd, ulong? address, bool info = false)
        {
            return SendCmdAsync<T>(_board, boardClass, instance, cmd, default, address).Result;
        }

        internal override Commands.BoardResponse<T> SetBoardData<T>(BoardClass boardClass, byte instance, BoardCommand<T> cmd, T value, ulong? address)
        {
            return SendCmdAsync<T>(_board, boardClass, instance, cmd, value, address).Result;
        }
    }
}