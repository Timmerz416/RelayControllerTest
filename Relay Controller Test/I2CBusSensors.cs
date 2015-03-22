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
		public HTU21DBusSensor() : base(BUS_ADDRESS, CLOCK_SPEED) { }

		//=====================================================================
		// readTemperature
		//=====================================================================
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
		// Class Constructor
		//=====================================================================
		public TSL2561BusSensor() : base(BUS_ADDRESS, CLOCK_SPEED) { }
	}
}
