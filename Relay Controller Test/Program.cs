using System;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using NETMF.OpenSource.XBee;
using NETMF.OpenSource.XBee.Api;
using NETMF.OpenSource.XBee.Api.Zigbee;

namespace RelayControllerTest {

	public class Program {
		//===========================================================================
		// PORT SETUP
		//===========================================================================
		// Analog input ports
		private static AnalogInput pwrInput = new AnalogInput(AnalogChannels.ANALOG_PIN_A0);	// Analog input to read thermostat power status
		private static AnalogInput tmpInput = new AnalogInput(AnalogChannels.ANALOG_PIN_A1);	// Analog input to read temperature
		private static AnalogInput lumInput = new AnalogInput(AnalogChannels.ANALOG_PIN_A2);	// Analog input to read the luminosity

		// Digital output ports
		private static OutputPort pwrStatusOutput = new OutputPort(Cpu.Pin.GPIO_Pin8, false);		// Output port for power led
		private static OutputPort relayStatusOutput = new OutputPort(Cpu.Pin.GPIO_Pin9, false);		// Output port for relay status led

		//===========================================================================
		// THERMOSTAT CONTROL MEMBERS
		//===========================================================================
		// Basic status members
		private static bool thermoOn = true;	// Keeps track of whether the thermostat is on or off
		private static bool relayOn = false;	// Keeps track of whether the relay is on or off
		private static bool overrideOn = false;	// Keeps track of whether the programming override mode is on or off

		//===========================================================================
		// XBEE SETUP
		//===========================================================================
		// XBee sensor codes
		private enum XBeePortData { Temperature, Luminosity, Pressure, Humidity, LuminosityLux, HeatingOn, ThermoOn, Power }

		// XBee command codes
		const byte CMD_ACK			= 0;
		const byte CMD_NACK			= 1;
		const byte CMD_THERMO_POWER	= 2;
		const byte CMD_OVERRIDE		= 3;
		const byte CMD_RULE_CHANGE	= 4;
		const byte CMD_SENSOR_DATA	= 5;

		// XBee subcommand codes
		const byte STATUS_OFF		= 0;
		const byte STATUS_ON		= 1;
		const byte STATUS_GET		= 2;
		const byte STATUS_ADD		= 3;
		const byte STATUS_DELETE	= 4;
		const byte STATUS_MOVE		= 5;
		const byte STATUS_UPDATE	= 6;

		// XBee Connection Members
		private static XBeeApi xBee;				// The object controlling the interface to the XBee radio
		private static bool xbeeConnected = false;	// A flag to indicate there is a connection to the XBee (true) or not (false)

		//===========================================================================
		// MAIN PROGRAM
		//===========================================================================
		public static void Main() {
			//--------------------------------------------------------------------------
			// INITIALIZE THE RADIOS AND THE TIMERS
			//--------------------------------------------------------------------------
			// Initialize the XBee
			Debug.Print("Initializing XBee...");
			xBee = new XBeeApi("COM1", 9600);	// RX and TX lines connected to digital pins 0 and 1 for COM1
			xBee.EnableDataReceivedEvent();
			xBee.EnableAddressLookup();
			xBee.EnableModemStatusEvent();

			try {
				// Connect to the XBee
				xBee.Open();
				xbeeConnected = true;
				Debug.Print("XBee Connected...");
			} catch(Exception xbeeIssue) {
				Debug.Print("Caught the following trying to open the XBee connection: " + xbeeIssue.Message);
			}

			// Setup and start the timer
			Timer dataPoll = new Timer(new TimerCallback(OnTimer), null, 0, 10000);	// Timer fires every 10 seconds, for development purposes

			//--------------------------------------------------------------------------
			// INFINTE LOOP TO CHECK POWER STATUS
			//--------------------------------------------------------------------------
			while(true) {
				// Check the status of the thermostat based on power from on/off switch (high = on; low = off)

			}
		}

		//---------------------------------------------------------------------------
		// Method to respond to timer events
		//---------------------------------------------------------------------------
		private static void OnTimer(Object dataObj) {
			Debug.Print("Timer event occurred.");
		}
	}
}
