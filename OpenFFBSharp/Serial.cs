using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using static OpenFFBoard.Commands;

namespace OpenFFBoard
{
    public class Serial : Board
    {
        private readonly SerialPort _serialPort;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private const int Timeout = 500;

        internal class SerialCommand<T>
        {
            public readonly BoardClass boardClass;
            public readonly byte? instance;
            public readonly BoardCommand cmd;
            public readonly ulong? address;
            public readonly T data;
            public readonly bool info = false;

            public SerialCommand(BoardClass boardClass, byte? instance, BoardCommand cmd, ulong? address, T data, bool info = false)
            {
                this.boardClass = boardClass;
                this.instance = instance;
                this.cmd = cmd;
                this.address = address;
                this.data = data;
                this.info = info;
            }
        }

        public Serial(string comPort, int baudRate)
        {
            _serialPort = new SerialPort(comPort, baudRate);
        }

        public override Task ConnectAsync()
        {
            Connect();
            return Task.CompletedTask;
        }

        public override void Connect()
        {
            _serialPort.Handshake = Handshake.None;
            _serialPort.Parity = Parity.None;
            _serialPort.DataBits = 8;
            _serialPort.StopBits = StopBits.One;
            _serialPort.DtrEnable = true;
            _serialPort.ReadTimeout = 200;
            _serialPort.WriteTimeout = 50;
            try
            {
                _serialPort.Open();
                IsConnected = _serialPort.IsOpen;
            }
            catch(Exception ex)
            {
                IsConnected = false;
                throw new IOException("Could not connect to the OpenFFBoard on " + _serialPort.PortName, ex);
            }
        }

        public override void Disconnect()
        {
            _serialPort.Close();
            IsConnected = _serialPort.IsOpen;
        }

        public static string[] GetBoards()
        {
            //TODO Filter to just OpenFFBoards
            return SerialPort.GetPortNames();
        }

        internal override Commands.BoardResponse<T> GetBoardData<T>(BoardClass boardClass, byte? instance, BoardCommand<T> cmd, ulong? address, bool info = false)
        {
            SerialCommand<T> command = new SerialCommand<T>(boardClass, instance, cmd, address, default, info);
            return Task.Run(() => SendCmd(command)).Result;
        }

        internal override Commands.BoardResponse<T> SetBoardData<T>(BoardClass boardClass, byte instance, BoardCommand<T> cmd, T value, ulong? address)
        {
            SerialCommand<T> command = new SerialCommand<T>(boardClass, instance, cmd, address, value);
            return Task.Run(() => SendCmd(command)).Result;
        }

        internal async Task<BoardResponse<T>> SendCmd<T>(SerialCommand<T> cmd)
        {
            await _semaphore.WaitAsync();
            string response;
            try
            {
                await WriteLineAsync(ConstructMessage(cmd));

                var task = ReadCommandAsync();
                if (await Task.WhenAny(task, Task.Delay(Timeout)) == task)
                {
                    await task;
                    response = task.Result;
                }
                else
                {
                    return null;
                }
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                _semaphore.Release();
            }
            return Task.Run(() => ParseBoardResponse(cmd, response)).Result;
        }

        /// <summary>
        /// Read a line from the SerialPort asynchronously
        /// </summary>
        /// <returns>A raw command read from the input</returns>
        public async Task<string> ReadCommandAsync()
        {
            try
            {
                byte[] buffer = new byte[1];
                string ret = string.Empty;

                // Read the input one byte at a time, convert the
                // byte into a char, add that char to the overall
                // response string, once the response string ends
                // with the line ending then stop reading
                while (true)
                {
                    await _serialPort.BaseStream.ReadAsync(buffer, 0, 1);
                    ret += _serialPort.Encoding.GetString(buffer);

                    if (ret.EndsWith("]"))
                        return ret.Trim();
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                throw new IOException("Could not read data from the OpenFFBoard.", ex);
            }
        }

        /// <summary>
        /// Write a line to the SerialPort asynchronously
        /// </summary>
        /// <param name="str">The text to send</param>
        /// <returns></returns>
        public async Task WriteLineAsync(string str)
        {
            try
            {
                byte[] encodedStr =
                    _serialPort.Encoding.GetBytes(str + _serialPort.NewLine);

                await _serialPort.BaseStream.WriteAsync(encodedStr, 0, encodedStr.Length);
                await _serialPort.BaseStream.FlushAsync();
            }
            catch (Exception ex)
            {
                IsConnected = false;
                throw new IOException("Could not write data to the OpenFFBoard.", ex);
            }
            
        }

        public async Task<string> SendRawMessage(string message)
        {
            Debug.WriteLine($"Raw message waiting for place in serial queue: {message}");
            await _semaphore.WaitAsync();
            Debug.WriteLine($"Place found, sending raw message: {message}");
            string response;
            try
            {
                await WriteLineAsync(message);

                var task = ReadCommandAsync();
                if (await Task.WhenAny(task, Task.Delay(Timeout)) == task)
                {
                    await task;
                    response = task.Result;
                    Debug.WriteLine($"Raw message response received: {response}");
                }
                else
                {
                    return null;
                }
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                _semaphore.Release();
            }

            return response;
        }

        private static BoardResponse<T> ParseBoardResponse<T>(SerialCommand<T> cmd, string response)
        {
            if (!response.StartsWith("[")) return null;

            response = response.TrimStart('[');
            response = response.TrimEnd(']');
            string[] splitResponse = response.Split('|');
            if (splitResponse[0] == ConstructMessage(cmd))
            {
                string[] splitData = splitResponse[1].Split(new char[':'], 1);
                string responseData;
                ulong responseAddress;
                if (splitData.Length >= 2)
                {
                    responseData = splitData[0];
                    responseAddress = Convert.ToUInt64(splitData[1]);
                }
                else
                {
                    responseData = splitResponse[1];
                    responseAddress = cmd.address ?? 0;
                }

                T data;

                if (((string)Convert.ChangeType(responseData, typeof(string))).Equals("OK"))
                {
                    data = default;
                }
                else if (typeof(T) == typeof(bool))
                {
                    data = (T)Convert.ChangeType(Convert.ToString(responseData).Equals("1"), typeof(T));
                }
                else
                {
                    data = (T)Convert.ChangeType(responseData, typeof(T));
                }

                return new Commands.BoardResponse<T>
                {
                    Type = GetCmdType(cmd),
                    ClassId = cmd.boardClass.ClassId,
                    Instance = cmd.instance ?? 0,
                    Cmd = cmd.cmd,
                    Data = data,
                    Address = responseAddress
                };
            }
            else
            {
                //response command doesn't match what was sent
                if (splitResponse[0] == "sys.0.errors?")
                {
                    //Error message
                    string[] splitData = splitResponse[1].Trim().Split(':');
                    return new Commands.BoardResponse<T>
                    {
                        Type = Commands.CmdType.Error,
                        ClassId = cmd.boardClass.ClassId,
                        Instance = cmd.instance ?? 0,
                        Cmd = cmd.cmd,
                        Data = default,
                        Address = 0
                    };
                }
            }

            return null;
        }

        private static CmdType GetCmdType<T>(SerialCommand<T> cmd)
        {
            if (cmd.info)
                return CmdType.Info;
            if (cmd.address != null && (cmd.data == null || cmd.data.Equals(default(T))))
                return CmdType.RequestAddress;
            if (cmd.address == null && cmd.data == null || cmd.data.Equals(default(T)))
                return CmdType.Request;
            if (cmd.address == null)
                return CmdType.Write;

            return CmdType.WriteAddress;
        }

        private static string ConstructMessage<T>(BoardClass classId, byte? instance, BoardCommand cmd, ulong? address, T data, bool info)
        {
            CmdType type = GetCmdType(new SerialCommand<T>(classId, instance, cmd, address, data, info));

            string cmdBuffer = classId.Prefix + ".";
            if (instance != null)
                cmdBuffer += $"{instance}.";
            cmdBuffer += cmd.Name;

            string stringData;
            if (typeof(T) == typeof(bool))
            {
                stringData = (bool)Convert.ChangeType(data, typeof(bool)) ? "1" : "0";
            }
            else if (typeof(T) == typeof(byte[]))
            {
                long hidData = 0;
                var bytes = (byte[])Convert.ChangeType(data, typeof(byte[]));
                hidData = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    hidData |= (long)bytes[i] << (i * 8);
                }
                stringData = Convert.ToString(hidData);
            }
            else
            {
                stringData = (string)Convert.ChangeType(data, typeof(string));
            }

            switch (type)
            {
                case CmdType.Request:
                    cmdBuffer += '?';
                    break;
                case CmdType.RequestAddress:
                    cmdBuffer += '?';
                    cmdBuffer += address;
                    break;
                case CmdType.Write:
                    cmdBuffer += '=';
                    cmdBuffer += stringData;
                    break;
                case CmdType.WriteAddress:
                    cmdBuffer += '=';
                    cmdBuffer += stringData;
                    cmdBuffer += '?';
                    cmdBuffer += address;
                    break;
                case CmdType.Info:
                    cmdBuffer += '!';
                    break;
            }
            return cmdBuffer;
        }

        private static string ConstructMessage<T>(SerialCommand<T> command)
        {
            return ConstructMessage(
                command.boardClass, 
                command.instance, 
                command.cmd, 
                command.address, 
                command.data,
                command.info);
        }

        /// <summary>
        /// Get the name of the COM port
        /// </summary>
        /// <returns>COM port name (typically COMx)</returns>
        public string GetPort()
        {
            return _serialPort.PortName;
        }
    }
}
