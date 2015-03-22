using System;
using Microsoft.SPOT;
using System.Threading;

namespace RelayControllerTest {

	//=========================================================================
	// HTU21DSensor Class
	//=========================================================================
	/// <summary>
	/// Implementation of the I2C protocal for a HTU21D sensor, specifically the Sparkfun one.
	/// </summary>
	class HTU21DSensor : I2CBreakout {
		//=====================================================================
		// CLASS CONSTANTS
		//=====================================================================
		// The address of the sensor
		private const int BUS_ADDRESS = 0x40;

		// The HTU21D Commands
		private const byte MEASURE_TEMPERATURE_HOLD		= 0xe3;
		private const byte MEASURE_TEMPERATURE_NOHOLD	= 0xf3;
		private const byte MEASURE_HUMIDITY_HOLD		= 0xe5;
		private const byte MEASURE_HUMIDITY_NOHOLD		= 0xf5;
		private const byte WRITE_USER_REGISTER			= 0xe6;
		private const byte READ_USER_REGISTER			= 0xe7;
		private const byte SOFT_RESET					= 0xfe;

		//=====================================================================
		// Default Constructor
		//=====================================================================
		public HTU21DSensor() : base(BUS_ADDRESS) { }

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
}
