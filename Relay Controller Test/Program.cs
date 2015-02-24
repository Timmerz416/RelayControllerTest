using System;
using System.Threading;
using System.Collections;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using NETMF.OpenSource.XBee;
using NETMF.OpenSource.XBee.Api;
using NETMF.OpenSource.XBee.Api.Zigbee;
using DayType = RelayControllerTest.TemperatureRule.DayType;

namespace RelayControllerTest {

	public class Program {
		//=====================================================================
		// PORT SETUP
		//=====================================================================
		// Analog input ports
		private static AnalogInput pwrInput = new AnalogInput(AnalogChannels.ANALOG_PIN_A0);	// Analog input to read thermostat power status
		private static AnalogInput tmpInput = new AnalogInput(AnalogChannels.ANALOG_PIN_A1);	// Analog input to read temperature
		private static AnalogInput lumInput = new AnalogInput(AnalogChannels.ANALOG_PIN_A2);	// Analog input to read the luminosity

		// Digital output ports
		private static OutputPort pwrStatusOutput = new OutputPort(Pins.GPIO_PIN_D8, false);		// Output port for power led
		private static OutputPort relayStatusOutput = new OutputPort(Pins.GPIO_PIN_D9, false);		// Output port for relay status led

		//=====================================================================
		// THERMOSTAT CONTROL MEMBERS
		//=====================================================================
		// Basic status members
		private static bool thermoOn = true;	// Keeps track of whether the thermostat is on or off
		private static bool relayOn = false;	// Keeps track of whether the relay is on or off
		private static bool overrideOn = false;	// Keeps track of whether the programming override mode is on or off

		// Timing variables
		private const int CONTROL_INTERVAL = 10000;	// The number of microseconds between control evaluations
		private const int SENSOR_PERIODS = 6;		// The number of control periods before a sensor evaluation
		private static int controlLoops = 0;		// Tracks the current number of control loops without a sensor loop

		// Thermostat rule variables
		private static ArrayList rules;	// Array holding the thermostat rules
		private const double MIN_TEMPERATURE = 16.0;	// Below this temperature, the relay opens no matter the programming
		private const double MAX_TEMPERATURE = 26.0;	// Above this temperature, the relay closes no matter the programming

		// Constants
		private const double TEMP_UNDEFINED = 200.0;	// High temperature values signifies it has not been set

		//=====================================================================
		// XBEE SETUP
		//=====================================================================
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

		//=====================================================================
		// MAIN PROGRAM
		//=====================================================================
		public static void Main() {
			//-----------------------------------------------------------------
			// INITIALIZE THE TIME, RADIOS, TIMERS AND RULES
			//-----------------------------------------------------------------
			// Set the time on the netduino
			Utility.SetLocalTime(new DateTime(2015, 2, 22, 9, 0, 0));

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

			// Create the default rules
			rules = new ArrayList();
			rules.Add(new TemperatureRule(DayType.Weekdays, 23.5, 19.0));
			rules.Add(new TemperatureRule(DayType.Weekdays, 16.5, 22.0));
			rules.Add(new TemperatureRule(DayType.Weekdays,  9.0, 18.0));
			rules.Add(new TemperatureRule(DayType.Weekdays,  7.0, 22.0));
			rules.Add(new TemperatureRule(DayType.Weekends, 23.5, 19.0));
			rules.Add(new TemperatureRule(DayType.Weekends,  7.5, 22.0));

			// Setup and start the timer
			Timer dataPoll = new Timer(new TimerCallback(OnTimer), null, 5000, CONTROL_INTERVAL);	// Timer delays for 5 seconds first time around, then every control interval

			//-----------------------------------------------------------------
			// INFINTE LOOP TO CHECK POWER STATUS
			//-----------------------------------------------------------------
			while(true) {
				// Check the status of the thermostat based on power from on/off switch (high = on; low = off)
				double powerLevel = 3.3*pwrInput.Read();	// The .Read() method return the fraction of the full pin voltage (3.3 V), with some offset which isn't important for this basic switch

				// Evaluate the thermostat and relay control based on the current voltage level
				if((powerLevel > 1.5) && !thermoOn) {	// Turn on the thermostat if previously off
					// Update the thermostat status indicators
					thermoOn = true;	// Set the master flag
					pwrStatusOutput.Write(true);	// Turn on the thermostat status LED
					Debug.Print("Thermostat turned ON");

					// Determine the relay status
					SetRelay(false);	// Turn off the relay by default as the programming logic will evaluate its status
					EvaluateProgramming(true);	// Force a data update since the thermostat status changed
				} else if((powerLevel < 1.5) && thermoOn) {	// Turn off the thermostat if previously on
					// Update the thermostat status indicators
					thermoOn = false;	// Set the master flag
					pwrStatusOutput.Write(false);	// Turn off the thermostat status LED
					Debug.Print("Thermostat turned OFF");
					
					// Open the relay for external control
					SetRelay(true);	// Open the relay
					SendXBeeDataPacket(TEMP_UNDEFINED);	// Programming rules don't apply, but still need to send data update for thermostat and relay status change
				}
			}
		}

		//=====================================================================
		// TIMER EVENT METHOD
		//=====================================================================
		private static void OnTimer(Object dataObj) {
			// Determine what to evaluate and send by XBee, depending on thermostat status and type of loop
			controlLoops++;	// Increment the loop counter
			if(thermoOn) EvaluateProgramming(controlLoops == SENSOR_PERIODS);	// Thermostat is on, so evaluate the relay status through programming rules, only force XBee data if a sensor loop
			else if(controlLoops == SENSOR_PERIODS) SendXBeeDataPacket(TEMP_UNDEFINED);	// Thermostat is off, so only send XBee data if a sensor loop

			// Reset the counter, if needed
			if(controlLoops == SENSOR_PERIODS) controlLoops = 0;
		}

		//=====================================================================
		// METHOD TO OPERATE THE RELAY
		//=====================================================================
		private static void SetRelay(bool openRelay) {
			if(openRelay && !relayOn) {	// Turn on relay only when it's off
				relayOn = true;	// Set master flag
				relayStatusOutput.Write(true);	// Code only for testing - just turns on LED
			} else if(!openRelay && relayOn) {
				relayOn = false;	// Set master flag
				relayStatusOutput.Write(false);	// Code only for testing - just turns off LED
			}
		}

		//=====================================================================
		// METHOD TO EVALUATE THE PROGRAMMING RULES AND ISSUE RELAY ACTION
		//=====================================================================
		private static void EvaluateProgramming(bool forceUpdate) {
			//-----------------------------------------------------------------
			// COLLECT CONTROL CONDITIONS
			//-----------------------------------------------------------------
			// Get the tempeature reading
			double temperature = 100.0*(3.3*tmpInput.Read() - 0.5);

			// Get the time
			DateTime curTime = DateTime.Now;
			Debug.Print("Evaluating relay status at " + curTime.ToString() + ": ");

			//-----------------------------------------------------------------
			// TEMPERATURE LIMITS CHECK
			//-----------------------------------------------------------------
			if(temperature < MIN_TEMPERATURE) {	// Temperature too low
				Debug.Print("\tRelay turned on due to low temperature");
				SetRelay(true);	// Turn on relay
				SendXBeeDataPacket(temperature);	// Dispatch change of relay state
			} else if(temperature >= MAX_TEMPERATURE) {	// Temperature above limit
				Debug.Print("\tRelay turned off due to high temperature");
				SetRelay(false);	// Turn off relay
				SendXBeeDataPacket(temperature);	// Dispatch change of relay state
			} else {	// Temperature is within limits, so evaluate relay status based on rules in effect
				//-------------------------------------------------------------
				// EVALUATE RELAY STATUS AGAINST PROGRAMMING
				//-------------------------------------------------------------
			}
		}

		//=====================================================================
		// METHOD TO SEND DATA PACKET THROUGH THE XBEE
		//=====================================================================
		private static void SendXBeeDataPacket(double temperature) {
		}
	}
}
