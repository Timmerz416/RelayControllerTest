using System;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;

namespace RelayControllerTest {

	//=========================================================================
	// I2CBreakout Class
	//=========================================================================
	/// <summary>
	/// Base class for I2C device communications based on the code in http://blog.codeblack.nl/post/NetDuino-Getting-Started-with-I2C.aspx
	/// </summary>
	public class I2CBreakout : IDisposable {
		//=====================================================================
		// CLASS CONSTANTS
		//=====================================================================
		private const int DEFAULT_CLOCK_RATE = 400;		// Default clock rate in kHz
		private const int TRANSACTION_TIMEOUT = 1000;	// Default transaction wait time in ms
 
		//=====================================================================
		// CLASS MEMBERS
		//=====================================================================
		private I2CDevice.Configuration _i2cConfig;	// Stores the configuration for the device
		private I2CDevice _i2cDevice;				// The object to interact with the I2C device
 
		public byte Address { get; private set; }	// The address of the device on the I2C bus
 
		public I2CBreakout(byte address, int clockRatekHz) {
			this.Address = address;
			this._i2cConfig = new I2CDevice.Configuration(this.Address, clockRatekHz);
			this._i2cDevice = new I2CDevice(this._i2cConfig);
		}

		public I2CBreakout(byte address) : this(address, DEFAULT_CLOCK_RATE) { }
 
		protected void Write(byte[] writeBuffer) {
			// create a write transaction containing the bytes to be written to the device
			I2CDevice.I2CTransaction[] writeTransaction = new I2CDevice.I2CTransaction[] {  I2CDevice.CreateWriteTransaction(writeBuffer)  };
 
			// write the data to the device
			int written = this._i2cDevice.Execute(writeTransaction, TRANSACTION_TIMEOUT);
 
			while (written < writeBuffer.Length) {
				byte[] newBuffer = new byte[writeBuffer.Length - written];
				Array.Copy(writeBuffer, written, newBuffer, 0, newBuffer.Length);
 
				writeTransaction = new I2CDevice.I2CTransaction[] {  I2CDevice.CreateWriteTransaction(newBuffer)  };
 
				written += this._i2cDevice.Execute(writeTransaction, TRANSACTION_TIMEOUT);
			}
 
			// make sure the data was sent
			if (written != writeBuffer.Length)
				throw new I2CException("Could not write to device.");
		}

		protected void Read(byte[] readBuffer) {
			// create a read transaction
			I2CDevice.I2CTransaction[] readTransaction = new I2CDevice.I2CTransaction[] {  I2CDevice.CreateReadTransaction(readBuffer)  };
 
			// read data from the device
			int read = this._i2cDevice.Execute(readTransaction, TRANSACTION_TIMEOUT);
 
			// make sure the data was read
			if (read != readBuffer.Length)
				throw new I2CException("Could not read from device.");
		}
 
		protected void WriteToRegister(byte register, byte value) {
			this.Write(new byte[] { register, value });
		}

		protected void WriteToRegister(byte register, byte[] values) {
			// create a single buffer, so register and values can be send in a single transaction
			byte[] writeBuffer = new byte[values.Length + 1];
			writeBuffer[0] = register;
			Array.Copy(values, 0, writeBuffer, 1, values.Length);
 
			this.Write(writeBuffer);
		}

		protected void ReadFromRegister(byte register, byte[] readBuffer) {
			this.Write(new byte[] { register });          
			this.Read(readBuffer);
		}

		public void Dispose() {
			_i2cDevice.Dispose();
		}
	}

	//=========================================================================
	// I2CException Class
	//=========================================================================
	public class I2CException : Exception {
		//=====================================================================
		// Basic Constructor
		//=====================================================================
		public I2CException(string message) : base(message) { }
	}
}
