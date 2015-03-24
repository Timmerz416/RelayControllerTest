using System;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System.Threading;

namespace RelayControllerTest {
	//=========================================================================
	// BasicI2CBusSensor Class
	//=========================================================================
	class BasicI2CBusSensor : I2CBus {
		//=====================================================================
		// CLASS MEMBERS
		//=====================================================================
		// I2C device configuration properties
		public I2CDevice.Configuration _config;

		//=====================================================================
		// Class Constructor
		//=====================================================================
		public BasicI2CBusSensor(ushort address, int clockSpeed) : base() {
			_config = new I2CDevice.Configuration(address, clockSpeed);
		}

		//=====================================================================
		// Write Override
		//=====================================================================
		protected void Write(byte[] writeBuffer, int transactionTimeout = DEFAULT_TIMEOUT) {
			base.Write(_config, writeBuffer, transactionTimeout);
		}

		//=====================================================================
		// Read Override
		//=====================================================================
		protected void Read(byte[] readBuffer, int transactionTimeout = DEFAULT_TIMEOUT) {
			base.Read(_config, readBuffer, transactionTimeout);
		}

		//=====================================================================
		// ReadRegister Override
		//=====================================================================
		protected void ReadRegister(byte register, byte[] readBuffer, int transactionTimeout = DEFAULT_TIMEOUT) {
			base.ReadRegister(_config, register, readBuffer, transactionTimeout);
		}

		//=====================================================================
		// WriteRegister - Array of Bytes
		//=====================================================================
		protected void WriteRegister(byte register, byte[] writeBuffer, int transactionTimeout = DEFAULT_TIMEOUT) {
			base.WriteRegister(_config, register, writeBuffer, transactionTimeout);
		}

		//=====================================================================
		// WriteRegister - Single Byte
		//=====================================================================
		protected void WriteRegister(byte register, byte writeBuffer, int transactionTimeout = DEFAULT_TIMEOUT) {
			base.WriteRegister(_config, register, writeBuffer, transactionTimeout);
		}
	}

	//=========================================================================
	// HTU21DBusSensor
	//=========================================================================
	class HTU21DBusSensor : BasicI2CBusSensor {
		//=====================================================================
		// CLASS CONSTANTS
		//=====================================================================
		// Set bus device properties
		private const ushort BUS_ADDRESS = 0x40;
		private const int CLOCK_SPEED = 400;

		// The HTU21D Commands
		private const byte MEASURE_TEMPERATURE_HOLD		= 0xE3;
		private const byte MEASURE_TEMPERATURE_NOHOLD	= 0xF3;
		private const byte MEASURE_HUMIDITY_HOLD		= 0xE5;
		private const byte MEASURE_HUMIDITY_NOHOLD		= 0xF5;
		private const byte WRITE_USER_REGISTER			= 0xE6;
		private const byte READ_USER_REGISTER			= 0xE7;
		private const byte SOFT_RESET					= 0xFE;

		//=====================================================================
		// Class Constructor
		//=====================================================================
		/// <summary>
		/// Set the address of the humidity sensor and set the clock speed
		/// </summary>
		public HTU21DBusSensor() : base(BUS_ADDRESS, CLOCK_SPEED) { }

		//=====================================================================
		// readTemperature
		//=====================================================================
		/// <summary>
		/// Read the temperature from the humidity sensor
		/// </summary>
		/// <returns>The measured temperature in Celsius</returns>
		public double readTemperature() {
			//-----------------------------------------------------------------
			// Implement the No Hold approach
			//-----------------------------------------------------------------
			// Signal for a measurement of the temperature
			Write(new byte[] { MEASURE_TEMPERATURE_NOHOLD });

			// Dealy for 60 ms while the sensor takes the measurement
			Thread.Sleep(60);	// Longest read time is 50 ms based on spec sheet, but add extra time

			// Read the resultant measurements - after delay, read 3 bytes
			byte[] buffer = new byte[3];
			Read(buffer);

			// TODO - CONFIRM CHECKSUM

			// Create raw measurement, minus the status bits
			uint rawTemperature = ((uint) buffer[0] << 8) | (uint) buffer[1];	// Combine the two measurement bytes
			uint statusBits = rawTemperature & 0x0003;	// Get the status bits
			rawTemperature &= 0xFFFC;	// Strip off the status bits

			// Confirm we have temperature data
			if(statusBits == 0) return 175.72*((double) rawTemperature)/65536.0 - 46.85;
			else throw new I2CException("Humidity measurement returns when requesting temperature measurement");
		}

		//=====================================================================
		// readHumidity
		//=====================================================================
		/// <summary>
		/// Read the humidity from the sensor
		/// </summary>
		/// <returns>The measured humidity in relative percent</returns>
		public double readHumidity() {
			//-----------------------------------------------------------------
			// Implement the No Hold approach
			//-----------------------------------------------------------------
			// Signal for a measurement of the temperature
			Write(new byte[] { MEASURE_HUMIDITY_NOHOLD });

			// Dealy for 60 ms while the sensor takes the measurement
			Thread.Sleep(60);	// Longest read time is 50 ms based on spec sheet, but add extra time

			// Read the resultant measurements - after delay, read 3 bytes
			byte[] buffer = new byte[3];
			Read(buffer);

			// TODO - CONFIRM CHECKSUM

			// Create raw measurement, minus the status bits
			uint rawHumidity = ((uint) buffer[0] << 8) | (uint) buffer[1];	// Combine the two measurement bytes
			uint statusBits = rawHumidity & 0x0003;	// Get the status bits
			rawHumidity &= 0xFFFC;	// Strip off the status bits

			// Confirm we have humidity data
			if(statusBits == 2) return 125.0*((double) rawHumidity)/65536.0 - 6.0;
			else throw new I2CException("Temperature measurement returns when requesting humidity measurement");
		}
	}

	//=========================================================================
	// TSL2561BusSensor
	//=========================================================================
	class TSL2561BusSensor : BasicI2CBusSensor {
		//=====================================================================
		// CLASS CONSTANTS
		//=====================================================================
		// Set bus device properties
		private const ushort BUS_ADDRESS = 0x39;
		private const int CLOCK_SPEED = 100;

		//=====================================================================
		// CLASS ENUMERATIONS
		//=====================================================================
		// Registers for TSL2561
		private enum Registers {
			Control				= 0x0,	// Control of basic functions
			Timing				= 0x1,	// Integration time/gain control
			ThresholdLowLow		= 0x2,	// Low byte of low interrupt threshold
			ThresholdLowHigh	= 0x3,	// High byte of low interrupt threshold
			ThresholdHighLow	= 0x4,	// Low byte of high interrupt threshold
			ThresholdHighHigh	= 0x5,	// High byte of high interrupt threshold
			Interrupt			= 0x6,	// Interrupt control
			ID					= 0xA,	// Part number / revision ID
			Data0Low			= 0xC,	// Low byte of ADC Channel 0
			Data0High			= 0xD,	// High byte of ADC Channel 0
			Data1Low			= 0xE,	// Low byte of ADC Channel 1
			Data1High			= 0xF	// High byte of ADC Channel 1
		}

		// Command options
		private enum CommandOptions {
			CommandBit	= 0x80,	// Identify the transaction as a command
			ClearBit	= 0x40,	// Clears pending interrupts
			WordBit		= 0x20,	// Indicates if a work (two bytes) are to be read/written to the device
			BlockBit	= 0x10	// Turn on blocking (1) or off (0)
		}

		// Device power options
		private enum PowerOptions {
			Off	= 0x00,	// Power down the device
			On	= 0x03	// Power up the device
		}

		// Gain options
		public enum GainOptions {
			Low		= 0x00,	// Low (x1) gain setting
			High	= 0x10	// High (x16) gain setting
		}

		// Integration time options
		public enum IntegrationOptions {
			Short	= 0x0,	// Shortest, 13.7 ms integration window
			Medium	= 0x1,	// Middle, 101 ms integration window
			Long	= 0x2,	// Longest 402 ms integration window
			Manual	= 0x8	// Manual integration window
		}

		//=====================================================================
		// Class Constructor
		//=====================================================================
		/// <summary>
		/// Set the address of the luminosity sensor and set the clock speed
		/// </summary>
		public TSL2561BusSensor() : base(BUS_ADDRESS, CLOCK_SPEED) { }

		//=====================================================================
		// PowerSensor
		//=====================================================================
		private void PowerSensor() {
			//-----------------------------------------------------------------
			// Power up the sensor
			//-----------------------------------------------------------------
			// Create the command and issue it
			byte command = (byte) CommandOptions.CommandBit | (byte) Registers.Control;
			WriteRegister(command, (byte) PowerOptions.On);
		}

		//=====================================================================
		// HibernateSensor
		//=====================================================================
		private void HibernateSensor() {
			//-----------------------------------------------------------------
			// Turn off sensor power
			//-----------------------------------------------------------------
			byte command = (byte) CommandOptions.CommandBit | (byte) Registers.Control;
			WriteRegister(command, (byte) PowerOptions.Off);
		}

		//=====================================================================
		// SetTiming
		//=====================================================================
		public void SetTiming(GainOptions gain, IntegrationOptions integration) {
			// Set the command and issue
			byte command = (byte) CommandOptions.CommandBit | (byte) Registers.Timing;
			byte options = (byte) ((byte) gain | (byte) integration);
			WriteRegister(command, options);
		}
	}
}