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
using RuleDays = RelayControllerTest.TemperatureRule.RuleDays;
using AnalogChannels = SecretLabs.NETMF.Hardware.Netduino.AnalogChannels;

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
		private const double tempBuffer = 0.5;			// The buffer to apply to the target temperature in evaluation relay status

		// Constants
		private const double TEMP_UNDEFINED = 200.0;	// High temperature values signifies it has not been set

		//=====================================================================
		// XBEE SETUP
		//=====================================================================
		// XBee sensor codes
		private enum XBeePortData { Temperature, Luminosity, Pressure, Humidity, LuminosityLux, HeatingOn, ThermoOn, Power }

		// XBee data codes
		const byte TEMPERATURE_CODE	=   1;
		const byte LUMINOSITY_CODE	=   2;
		const byte PRESSURE_CODE	=   4;
		const byte HUMIDITY_CODE	=   8;
		const byte POWER_CODE		=  16;
		const byte LUX_CODE			=  32;
		const byte HEATING_CODE		=  64;
		const byte THERMOSTAT_CODE	= 128;

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
			rules.Add(new TemperatureRule(RuleDays.Weekdays, 23.5, 19.0));
			rules.Add(new TemperatureRule(RuleDays.Weekdays, 16.5, 22.0));
			rules.Add(new TemperatureRule(RuleDays.Weekdays,  9.0, 18.0));
			rules.Add(new TemperatureRule(RuleDays.Weekdays,  7.0, 22.0));
			rules.Add(new TemperatureRule(RuleDays.Weekends, 23.5, 19.0));
			rules.Add(new TemperatureRule(RuleDays.Weekends,  7.5, 22.0));

			// Initialize the relay status and power indicator
			pwrStatusOutput.Write(thermoOn);
			SetRelay(relayOn);

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

			// Get the time and weekday for evaluating the rules
			double curTime = DateTime.Now.Hour + DateTime.Now.Minute/60.0 + DateTime.Now.Second/3600.0;
			RuleDays curWeekday = (RuleDays)((int) DateTime.Now.DayOfWeek);	// Cast the returned DayOfWeek enum into the custome DayType enum
			Debug.Print("Evaluating relay status on a " + curWeekday + " at " + curTime.ToString("F4") + " with measured temperature at " + temperature.ToString("F") + ": ");

			//-----------------------------------------------------------------
			// TEMPERATURE LIMITS CHECK
			//-----------------------------------------------------------------
			bool updatePacket = forceUpdate;	// Default value for update data packet is from a parameter for a forced update
			if(temperature < MIN_TEMPERATURE) {	// Temperature too low
				Debug.Print("\tRelay turned on due to low temperature");
				if(!relayOn) {
					SetRelay(true);	// Turn on relay
					updatePacket = true;	// Indicate to dispatch change of relay state
				}
			} else if(temperature >= MAX_TEMPERATURE) {	// Temperature above limit
				Debug.Print("\tRelay turned off due to high temperature");
				if(relayOn) {
					SetRelay(false);	// Turn off relay
					updatePacket = true;	// Indicate to dispatch change of relay state
				}
			} else {	// Temperature is within limits, so evaluate relay status based on rules in effect
				//-------------------------------------------------------------
				// EVALUATE RELAY STATUS AGAINST PROGRAMMING
				//-------------------------------------------------------------
				// Iterate through the rules
				bool ruleFound = false;	// Flags that a rule has been found
				while(!ruleFound) {
					// Iterate through the rules until the active one is found
					for(int i = 0; i < rules.Count; i++) {
						// Check to see if current rule applies
						TemperatureRule curRule = rules[i] as TemperatureRule;
						if(RuleApplies(curRule, curWeekday, curTime)) {
							// Rule applies, now determine how to control the relay
							if(relayOn && (temperature > (curRule.Temperature + tempBuffer))) {
								// Temperature exceeding rule, turn off relay
								SetRelay(false);
								updatePacket = true;	// Indicate to send the updated status
								Debug.Print("\tRelay turned OFF since temperature (" + temperature.ToString("F") + ") is greater than the unbuffered rule temperatre (" + curRule.Temperature.ToString("F") + ")");
							} else if(!relayOn && (temperature < (curRule.Temperature - tempBuffer))) {
								// Temperature below rule, turn on relay
								SetRelay(true);
								updatePacket = true;	// Inidcate to send teh updated status
								Debug.Print("\tRelay turned ON since temperature (" + temperature.ToString("F") + ") is less than the unbuffered rule temperature (" + curRule.Temperature.ToString("F") + ")");
							} else {
								// No relay status change needed, but check for a forced status update
								Debug.Print("\tRelay remains " + (relayOn ? "ON" : "OFF"));
							}

							// Rule found, so break from the loops
							ruleFound = true;
							break;
						}
					}

					// No rule was found to apply, so move the day back before checking against rules again
					if(!ruleFound) {
						// Decrease the indicated day, but increase the time
						if(curWeekday == RuleDays.Sunday) curWeekday = RuleDays.Saturday;
						else curWeekday = (RuleDays) ((int) curWeekday - 1);
						curTime += 24.0;
					}
				}
			}

			//-----------------------------------------------------------------
			// SEND THE DATA
			//-----------------------------------------------------------------
			if(updatePacket) SendXBeeDataPacket(temperature);
		}

		//=====================================================================
		// METHOD TO CHECK IF TIME AND DATE MATCH THE RULE
		//=====================================================================
		private static bool RuleApplies(TemperatureRule rule, RuleDays checkDay, double checkTime) {
			// First check: time is later than the rule time
			if(checkTime >= rule.Time) {
				if(rule.Days == RuleDays.Everyday) return true; // The specific day doesn't matter in this case
				if(checkDay == rule.Days) return true;	// The day of the rule has been met
				if((rule.Days == RuleDays.Weekdays) && (checkDay >= RuleDays.Monday) && (checkDay <= RuleDays.Friday)) return true;	// The rule is for weekdays and this is met
				if((rule.Days == RuleDays.Weekends) && ((checkDay == RuleDays.Saturday) || (checkDay == RuleDays.Sunday))) return true;	// The rule is for weekend and this is met
			}

			return false;	// If a match hasn't been found, this rule doesn't apply and return false
		}

		//=====================================================================
		// METHOD TO SEND DATA PACKET THROUGH THE XBEE
		//=====================================================================
		private static void SendXBeeDataPacket(double temperature) {
			//-----------------------------------------------------------------
			// GET ANY DATA FOR THE TRANSMISSION
			//-----------------------------------------------------------------
			// Get temperature
			float floatTemp = (temperature == TEMP_UNDEFINED) ? 100f*(3.3f*((float) tmpInput.Read()) - 0.5f) : (float) temperature;	// Convert double to float

			// Get luminosity
			float luminosity = 3.3f*((float) lumInput.Read());

			// Get status indicators
			float power = 3.3f;
			float thermoStatus = thermoOn ? 1f : 0f;
			float relayStatus = relayOn ? 1f : 0f;

			//-----------------------------------------------------------------
			// CREATE THE BYTE ARRAYS AND TRANSMISSION PACKAGE
			//-----------------------------------------------------------------
			// Convert the floats to byte arrays
			byte[] tempBytes, luxBytes, powerBytes, thermoBytes, relayBytes;
			tempBytes = FloatToByte(floatTemp);
			luxBytes = FloatToByte(luminosity);
			powerBytes = FloatToByte(power);
			thermoBytes = FloatToByte(thermoStatus);
			relayBytes = FloatToByte(relayStatus);

			// Allocate the data package
			int floatSize = sizeof(float);
			Debug.Assert(floatSize == 4);
			byte[] package = new byte[5*(floatSize+1) + 1];	// Allocate memory for the package

			// Create the package of data
			package[0] = CMD_SENSOR_DATA;	// Indicate the package contains sensor data
			package[1] = TEMPERATURE_CODE;
			package[(floatSize+1)+1] = LUX_CODE;
			package[2*(floatSize+1)+1] = POWER_CODE;
			package[3*(floatSize+1)+1] = HEATING_CODE;
			package[4*(floatSize+1)+1] = THERMOSTAT_CODE;
			for(int i = 0; i < floatSize; i++) {
				package[i+2] = tempBytes[i];
				package[(floatSize+1)+(i+2)] = luxBytes[i];
				package[2*(floatSize+1)+(i+2)] = powerBytes[i];
				package[3*(floatSize+1)+(i+2)] = relayBytes[i];
				package[4*(floatSize+1)+(i+2)] = thermoBytes[i];
			}

			// Create the TxRequest packet
			XBeeAddress64 xbeeCoordinator = new XBeeAddress64("00 00 00 00 00 00 00 00");
			TxRequest txRequest = new TxRequest(xbeeCoordinator, package);

			//-----------------------------------------------------------------
			// TRANSMIST THE DATA
			//-----------------------------------------------------------------
			// Print transmission to the debugger
			string message = "Sending the following message (" + floatTemp.ToString("F") + ", " + luminosity.ToString("F") + ", " + power.ToString("F") + ", " + relayStatus.ToString("F0") + ", " + thermoStatus.ToString("F0") + "): ";
			for(int i = 0; i < package.Length; i++) {
				if(i != 0) message += "-";	// Add spacers between bytes
				message += package[i].ToString("X");	// Output byte as a hex number
			}
			Debug.Print(message);

			// Transmit through XBee
			if(xbeeConnected) {
				try {
					// Send the message and check for response
					XBeeResponse response = xBee.Send(txRequest).To(xbeeCoordinator).GetResponse();
					if(response is TxStatusResponse) {
						// Check status of the response
						TxStatusResponse txResponse = response as TxStatusResponse;
						if(!txResponse.IsSuccess) {
							// Print out error - TODO: save the data to a local data logger
							Debug.Print("Transmission came back with the following information: " + txResponse);
						}
					} else Debug.Print("Unusual response received from TxRequest");
				} catch(XBeeTimeoutException timeoutEx) {
					// Print out timeout - TODO: save the data to a local data logger
					Debug.Print("Transmission timed out - check for connection with coordinator");
				}
			} else Debug.Print("Cannot transmit XBee packet as the radio is not connected");
		}

		//=====================================================================
		// METHOD TO CONVERT A BYTE ARRAY TO A FLOAT
		//=====================================================================
		private static unsafe float ByteToFloat(byte[] byte_array) {
			uint ret = (uint) (byte_array[0] << 0 | byte_array[1] << 8 | byte_array[2] << 16 | byte_array[3] << 24);
			float r = *((float*) &ret);
			return r;
		}

		//=====================================================================
		// METHOD TO CONVERT A FLOAT TO A BYTE ARRAY
		//=====================================================================
		private static unsafe byte[] FloatToByte(float value) {
			Debug.Assert(sizeof(uint) == 4);	// Confirm that the int is a 4-byte variable

			uint asInt = *((uint*) &value);
			byte[] byte_array = new byte[sizeof(uint)];

			byte_array[0] = (byte) (asInt & 0xFF);
			byte_array[1] = (byte) ((asInt >> 8) & 0xFF);
			byte_array[2] = (byte) ((asInt >> 16) & 0xFF);
			byte_array[3] = (byte) ((asInt >> 24) & 0xFF);

			return byte_array;
		}

	}
}
