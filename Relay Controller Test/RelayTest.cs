﻿using System;
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
		private static double overrideTemp = 0;	// Contains the override temperature the thermostat is targetting

		// Timing variables
		private const int CONTROL_INTERVAL = 60000;	// The number of microseconds between control evaluations
		private const int SENSOR_PERIODS = 5;		// The number of control periods before a sensor evaluation
		private static int controlLoops = 0;		// Tracks the current number of control loops without a sensor loop
		private static bool sensorSent = false;		// Tracks whether the controller is waiting for a sensor acknowledgement

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
		const byte CMD_THERMO_POWER	= 1;
		const byte CMD_OVERRIDE		= 2;
		const byte CMD_RULE_CHANGE	= 3;
		const byte CMD_SENSOR_DATA	= 4;
		const byte CMD_TIME_REQUEST	= 5;

		// XBee subcommand codes
		const byte CMD_NACK			= 0;
		const byte CMD_ACK			= 1;
		const byte STATUS_OFF		= 2;
		const byte STATUS_ON		= 3;
		const byte STATUS_GET		= 4;
		const byte STATUS_ADD		= 5;
		const byte STATUS_DELETE	= 6;
		const byte STATUS_MOVE		= 7;
		const byte STATUS_UPDATE	= 8;

		// XBee Connection Members
		private static XBeeApi xBee;				// The object controlling the interface to the XBee radio
		private static bool xbeeConnected = false;	// A flag to indicate there is a connection to the XBee (true) or not (false)
		const string COORD_ADDRESS = "00 00 00 00 00 00 00 00";	// The address of the coordinator

		//=====================================================================
		// SENSOR SETUP
		//=====================================================================
		private static HTU21DBusSensor tempSensor = new HTU21DBusSensor();
		private static TSL2561BusSensor luxSensor = new TSL2561BusSensor();
		private static DS1307BusSensor timeKeeper = new DS1307BusSensor();

		//=====================================================================
		// MAIN PROGRAM
		//=====================================================================
		public static void Main() {
			//-----------------------------------------------------------------
			// INITIALIZE THE TIME, RADIOS, TIMERS AND RULES
			//-----------------------------------------------------------------
			// Set the time on the netduino on startup from the DS1307 clock
			Utility.SetLocalTime(timeKeeper.GetTime().getDateTime());

			// Initialize the XBee
			Debug.Print("Initializing XBee...");
			xBee = new XBeeApi("COM1", 9600);	// RX and TX lines connected to digital pins 0 and 1 for COM1
			xBee.EnableDataReceivedEvent();
			xBee.EnableAddressLookup();
			xBee.EnableModemStatusEvent();
			xBee.DataReceived += xBee_RequestReceived;
			NETMF.OpenSource.XBee.Util.Logger.Initialize(Debug.Print, NETMF.OpenSource.XBee.Util.LogLevel.All);

			// Connect to the XBee
			ConnectToXBee();

			// Create the default rules
			rules = new ArrayList();
			rules.Add(new TemperatureRule(RuleDays.Weekdays, 23.5, 19.0));
			rules.Add(new TemperatureRule(RuleDays.Weekdays, 16.5, 22.0));
			rules.Add(new TemperatureRule(RuleDays.Weekdays, 9.0, 18.0));
			rules.Add(new TemperatureRule(RuleDays.Weekdays, 7.0, 22.0));
			rules.Add(new TemperatureRule(RuleDays.Weekends, 23.5, 19.0));
			rules.Add(new TemperatureRule(RuleDays.Weekends, 7.5, 22.0));

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
				if((powerLevel > 1.5) && !thermoOn) SetPowerMode(true);			// Turn on the thermostat if previously off
				else if((powerLevel < 1.5) && thermoOn) SetPowerMode(false);	// Turn off the thermostat if previously on
			}
		}

		//=====================================================================
		// ConnectToXBee
		//=====================================================================
		/// <summary>
		/// The method to connect to the XBee through the API
		/// </summary>
		/// <returns>Whether the connection was successfull</returns>
		private static bool ConnectToXBee() {
			//-----------------------------------------------------------------
			// ONLY CONNECT IF NOT ALREADY CONNECTED
			//-----------------------------------------------------------------
			if(!xbeeConnected) {
				try {
					// Connect to the XBee
					xBee.Open();
					xbeeConnected = true;	// Set connected status
					Debug.Print("XBee Connected...");
				} catch(Exception xbeeIssue) {	// This assumes only xBee.Open command throws exceptions
					Debug.Print("Caught the following trying to open the XBee connection: " + xbeeIssue.Message);
					return false;	// Signal that the connection failed
				}
			}

			// If the code gets here, the xbee is connected
			return true;
		}

		//=====================================================================
		// XBEE DATA RECEIVED EVENT HANDLER (xBee_RequestReceived)
		//=====================================================================
		/// <summary>
		/// Function that handles the XBee data received event (TxRequest type)
		/// </summary>
		/// <param name="receiver">The API for the receiving XBee</param>
		/// <param name="data">The payload data from the request</param>
		/// <param name="sender">The address of the sending XBee</param>
		static void xBee_RequestReceived(XBeeApi receiver, byte[] data, XBeeAddress sender) {
			// Format the data packet to correct the escape charaters
			byte[] request = FormatApiMode(data, true);

			// Print out the received request
			string message = "Received the following message from " + sender.ToString() + ": ";
			for(int i = 0; i < request.Length; i++) message += request[i].ToString("X") + (i == (request.Length - 1) ? "" : "-");
			Debug.Print(message);

			// Process the request and get the response data
			byte[] response = ProcessRequest(request);

			// Send the response
			if(response != null) SendXBeeTransmission(FormatApiMode(response, false), sender);
		}

		//=====================================================================
		// ProcessRequest
		//=====================================================================
		/// <summary>
		/// Processes a request received through the XBee interface
		/// </summary>
		/// <param name="command">The data packet recieved in the XBee transmission</param>
		/// <returns>The response to send back to the sender of the transmission</returns>
		private static byte[] ProcessRequest(byte[] command) {
			// Setup the response packet
			byte[] dataPacket = null;

			//-----------------------------------------------------------------
			// DETERMINE THE TYPE OF PACKET RECEIVED AND ACT ACCORDINGLY 
			//-----------------------------------------------------------------
			switch(command[0]) {
				//-------------------------------------------------------------
				case CMD_THERMO_POWER:	// Command sent to power on/off the thermostat
					Debug.Print("Received command to change thermostat power status to " + (command[1] == STATUS_ON ? "ON" : "OFF") + " - NOT IMPLEMENTED IN HARDWARE");
					dataPacket = new byte[] { CMD_THERMO_POWER, CMD_ACK };	// Acknowledge the command
					break;
				//-------------------------------------------------------------
				case CMD_OVERRIDE:	// Command to turn on/off the override and set the target temperature
					dataPacket = new byte[] { CMD_OVERRIDE, CMD_ACK };	// By default, set the response to an acknowledgement

					// Check status flag
					switch(command[1]) {
						case STATUS_OFF:	// Turn off override mode
							overrideOn = false;	// Turn off override status
							Debug.Print("Received command to turn off override mode");
							break;
						case STATUS_ON:	// Turn on override mode
							// Convert the byte array to a float (1st byte is the command, 2nd to 5th bytes are the float)
							byte[] tempArray = new byte[4];
							for(int i = 0; i < 4; i++) tempArray[i] = command[i+2];	// Copy the byte array for the float
							overrideTemp = (double) ByteToFloat(tempArray);	// Set the target override temperature

							// Change override status
							overrideOn = true;	// Turn on override status
							Debug.Print("Received command to turn on override mode with target temperature of " + overrideTemp);
							break;
						default:	// Command not defined
							Debug.Print("Received command to override mode (" + command[1] + ") not implemented");
							dataPacket[1] = CMD_NACK;	// Indicate that the command is not understood
							break;
					}
					break;
				//-------------------------------------------------------------
				case CMD_RULE_CHANGE:	// A command to change/view the thermostat rules has been made
					// Take action based on the issued command
					switch(command[1]) {
						//-----------------------
						case STATUS_GET:
							dataPacket = ProcessGetRuleCMD();	// Get the rules and incorporate them into the response packet
							break;
						//-----------------------
						case STATUS_ADD:
							// Create default return packet
							dataPacket = new byte[] { CMD_RULE_CHANGE, STATUS_ADD, CMD_NACK };

							// Check that the index makes sense
							if(command[2] <= rules.Count) {
								// Create the rule floats
								byte[] tempArray = new byte[4];
								byte[] timeArray = new byte[4];
								for(int i = 0; i < 4; i++) {
									timeArray[i] = command[4+i];
									tempArray[i] = command[8+i];
								}
								float time = ByteToFloat(timeArray);
								float temp = ByteToFloat(tempArray);

								// Add the rule
								TemperatureRule newRule = new TemperatureRule((RuleDays) command[3], time, temp);
								rules.Insert(command[2], newRule);
								dataPacket[2] = CMD_ACK;
							}
							break;
						//-----------------------
						case STATUS_DELETE:
							// Create the default return packet
							dataPacket = new byte[] { CMD_RULE_CHANGE, STATUS_DELETE, CMD_NACK };

							// Delete the entry if it makes sense
							if(command[2] < rules.Count) {
								rules.RemoveAt(command[2]);
								dataPacket[2] = CMD_ACK;
							}
							break;
						//-----------------------
						case STATUS_MOVE:
							// Create the default return packet
							dataPacket = new byte[] { CMD_RULE_CHANGE, STATUS_MOVE, CMD_NACK };

							// Check that the indicies are valid
							if((command[2] < rules.Count) && (command[3] < rules.Count)) {
								// Copy the rule
								object moveRule = rules[command[2]];
								rules.RemoveAt(command[2]);
								rules.Insert(command[3], moveRule);

								dataPacket[2] = CMD_ACK;
							}
							break;
						//-----------------------
						case STATUS_UPDATE:
							// Create default return packet
							dataPacket = new byte[] { CMD_RULE_CHANGE, STATUS_UPDATE, CMD_NACK };

							// Check that the index makes sense
							if(command[2] < rules.Count) {
								// Create the rule floats
								byte[] tempArray = new byte[4];
								byte[] timeArray = new byte[4];
								for(int i = 0; i < 4; i++) {
									timeArray[i] = command[4+i];
									tempArray[i] = command[8+i];
								}
								float time = ByteToFloat(timeArray);
								float temp = ByteToFloat(tempArray);

								// Add the updated rule and delete the old one
								TemperatureRule updateRule = new TemperatureRule((RuleDays) command[3], time, temp);
								rules.Insert(command[2], updateRule);
								rules.RemoveAt(command[2] + 1);
								dataPacket[2] = CMD_ACK;
							}
							break;
						//-----------------------
						default:
							Debug.Print("Received command to rule change mode (" + command[1] + ") not implemented");
							dataPacket = new byte[] { CMD_RULE_CHANGE, CMD_NACK };
							break;
					}
					break;
				//-------------------------------------------------------------
				case CMD_SENSOR_DATA:	// Should only receive this for a sensor data acknoledgement packet
					// Check to see if a sensor data packet was sent recently
					if(sensorSent) {
						switch(command[1]) {
							case CMD_NACK:
								// TODO => Save the alst sensor data to the data logger
								Debug.Print("Need to write last sensor data to logger!");
								break;
							case CMD_ACK:
								sensorSent = false;	// Identify that there isn't a sensor reading not acknowledged
								Debug.Print("Sensor data acknowledged!");
								break;
							default:
								Debug.Print("Received command to sensor data mode (" + command[1] + ") not implemented");
								break;
						}
					} else Debug.Print("Receiving sensor command without having sent unacknowledged sensor data - not sure how this happened!");
					break;
				//-------------------------------------------------------------
				case CMD_TIME_REQUEST:	// Interact with the DS1307 on the I2C bus
					// Take action based on issued command
					switch(command[1]) {
						case STATUS_GET:	// Get the current time on the DS1307
							// Get the time from the RTC and create the packet
							DS1307BusSensor.RTCTime curTime = timeKeeper.GetTime();
							dataPacket = new byte[] { CMD_TIME_REQUEST, STATUS_GET, curTime.second, curTime.minute, curTime.hour, curTime.weekday, curTime.day, curTime.month, curTime.year };
							break;
						case STATUS_UPDATE:	// Set the time on the DS1307
							// Check that the data is there
							if(command.Length == 9) {
								// Convert to a time structure and send to DS1307
								DS1307BusSensor.RTCTime setTime = new DS1307BusSensor.RTCTime(command[2], command[3], command[4], command[6], command[7], command[8], (DS1307BusSensor.DayOfWeek) command[5]);
								timeKeeper.SetTime(setTime);
								dataPacket = new byte[] { CMD_TIME_REQUEST, STATUS_UPDATE, CMD_ACK };
							} else {
								// Return an NACK
								Debug.Print("Received command to set the time with incorrect number of command elements (" + command.Length + ")!");
								dataPacket = new byte[] { CMD_TIME_REQUEST, STATUS_UPDATE, CMD_NACK };
							}
							break;
						default:	// Command not implemented
							Debug.Print("Received command to time request mode (" + command[1] + ") not implemented");
							dataPacket = new byte[] { CMD_TIME_REQUEST, command[1], CMD_NACK };
							break;
					}
					break;
				//-------------------------------------------------------------
				default:// Acknowledge the command, even though it doesn't exist
					Debug.Print("TxRequest type has not been implemented yet");
					dataPacket = new byte[] { CMD_OVERRIDE, CMD_NACK };
					break;
			}

			// Return the response data
			return dataPacket;
		}

		//=====================================================================
		// ProcessGetRuleCMD
		//=====================================================================
		/// <summary>
		/// This method creates a XBee packet containing the rules and issues to the coordinator
		/// </summary>
		/// <returns>The response to send back to the sender of the transmission</returns>
		private static byte[] ProcessGetRuleCMD() {
			//-----------------------------------------------------------------
			// CREATE THE DATA PACKET
			//-----------------------------------------------------------------
			// Initialize the packet
			int numBytes = 9*rules.Count + 3;	// Get the size of the data packet
			byte[] packet = new byte[numBytes];	// Will contain the data
			packet[0] = CMD_RULE_CHANGE;
			packet[1] = STATUS_GET;
			packet[2] = (byte) rules.Count;

			// Add the rules
			for(int i = 0; i < rules.Count; i++) {
				// Convert the floating point values to byte arrays
				TemperatureRule curRule = rules[i] as TemperatureRule;	// Get the current rule as the TemperatureRule type
				byte[] timeArray, tempArray;
				timeArray = FloatToByte((float) curRule.Time);			// Convert the time
				tempArray = FloatToByte((float) curRule.Temperature);	// Convert the temperature

				// Copy byte arrays to the packet
				packet[9*i + 3] = (byte) curRule.Days;
				for(int j = 0; j < 4; j++) {
					packet[9*i + j + 4] = timeArray[j];
					packet[9*i + j + 8] = tempArray[j];
				}
			}

			//-----------------------------------------------------------------
			// Return the response packet
			//-----------------------------------------------------------------
			return packet;
		}

		//=====================================================================
		// SendXBeeResponse
		//=====================================================================
		/// <summary>
		/// Sends a TxRequest over the XBee network
		/// </summary>
		/// <param name="payload">The data payload for the transmission</param>
		/// <param name="destination">The XBee radio to send the data to</param>
		/// <returns>Where the transmission was successful</returns>
		private static bool SendXBeeTransmission(byte[] payload, XBeeAddress destination) {
			//-----------------------------------------------------------------
			// SEND PACKET TO DESTINATION
			//-----------------------------------------------------------------
			// Create the transmission object to the specified destination
			TxRequest response = new TxRequest(destination, payload);
			response.Option = TxRequest.Options.DisableAck;

			// Create debug console message
			string message = "Sending message to " + destination.ToString() + " (";
			for(int i = 0; i < payload.Length; i++) message += payload[i].ToString("X") + (i == (payload.Length - 1) ? "" : "-");
			message += ") => ";

			// Connect to the XBee
			bool sentMessage = false;
			if(ConnectToXBee()) {
				try {
					// Send the response
					xBee.Send(response).NoResponse();	// Send packet
					message += "Sent";
					sentMessage = true;
				} catch(XBeeTimeoutException) {
					message += "Timeout";
				}  // OTHER EXCEPTION TYPES TO INCLUDE?
			} else message += "XBee Disconnected";

			Debug.Print(message);
			return sentMessage;
		}

		//=====================================================================
		// SendSensorData
		//=====================================================================
		/// <summary>
		/// Send a sensor data package to the system logger.
		/// </summary>
		/// <param name="temperature">The measured temperature to send</param>
		private static void SendSensorData(double temperature) {
			//-----------------------------------------------------------------
			// GET ANY DATA FOR THE TRANSMISSION
			//-----------------------------------------------------------------
			// Get temperature
			float floatTemp = (temperature == TEMP_UNDEFINED) ? (float) tempSensor.readTemperature() : (float) temperature;	// Convert double to float
			Debug.Print("\tMeasured temperature = " + floatTemp);

			// Get luminosity
			//float luminosity = 3.3f*((float) lumInput.Read());
			luxSensor.SetTiming(TSL2561BusSensor.GainOptions.Low, TSL2561BusSensor.IntegrationOptions.Medium);
			float luminosity = (float) luxSensor.readOptimizedLuminosity();
			Debug.Print("\tMeasured luminosity = " + luminosity);

			// Get humidity
			float humidity = (float) tempSensor.readHumidity();
			Debug.Print("\tMeasured humidity = " + humidity);

			// Get status indicators
			float power = 3.3f;
			float thermoStatus = thermoOn ? 1f : 0f;
			float relayStatus = relayOn ? 1f : 0f;

			//-----------------------------------------------------------------
			// CREATE THE BYTE ARRAYS AND TRANSMISSION PACKAGE
			//-----------------------------------------------------------------
			// Convert the floats to byte arrays
			byte[] tempBytes, luxBytes, humidityBytes, powerBytes, thermoBytes, relayBytes;
			tempBytes = FloatToByte(floatTemp);
			luxBytes = FloatToByte(luminosity);
			humidityBytes = FloatToByte(humidity);
			powerBytes = FloatToByte(power);
			thermoBytes = FloatToByte(thermoStatus);
			relayBytes = FloatToByte(relayStatus);

			// Allocate the data package
			int floatSize = sizeof(float);
			Debug.Assert(floatSize == 4);
			byte[] package = new byte[6*(floatSize+1) + 1];	// Allocate memory for the package

			// Create the package of data
			package[0] = CMD_SENSOR_DATA;	// Indicate the package contains sensor data
			package[1] = TEMPERATURE_CODE;
			package[(floatSize+1)+1] = LUX_CODE;
			package[2*(floatSize+1)+1] = HUMIDITY_CODE;
			package[3*(floatSize+1)+1] = POWER_CODE;
			package[4*(floatSize+1)+1] = HEATING_CODE;
			package[5*(floatSize+1)+1] = THERMOSTAT_CODE;
			for(int i = 0; i < floatSize; i++) {
				package[i+2] = tempBytes[i];
				package[(floatSize+1)+(i+2)] = luxBytes[i];
				package[2*(floatSize+1)+(i+2)] = humidityBytes[i];
				package[3*(floatSize+1)+(i+2)] = powerBytes[i];
				package[4*(floatSize+1)+(i+2)] = relayBytes[i];
				package[5*(floatSize+1)+(i+2)] = thermoBytes[i];
			}

			//-----------------------------------------------------------------
			// TRANSMIST THE SENSOR DATA
			//-----------------------------------------------------------------			
			// Create the TxRequest packet and send the data
			XBeeAddress64 loggerAddress = new XBeeAddress64(COORD_ADDRESS);
			sensorSent = SendXBeeTransmission(package, loggerAddress);
/*			if(SendXBeeTransmission(package, loggerAddress)) {
				// Print transmission to the debugger
				string message = "Sent the following message (" + floatTemp.ToString("F") + ", " + luminosity.ToString("F") + ", " + power.ToString("F") + ", " + relayStatus.ToString("F0") + ", " + thermoStatus.ToString("F0") + "): ";
				for(int i = 0; i < package.Length; i++) {
					if(i != 0) message += "-";	// Add spacers between bytes
					message += package[i].ToString("X");	// Output byte as a hex number
				}
				Debug.Print(message);
			} else {
				Debug.Print("Error sending the sensor data");
				// TODO - DEVELOP CODE TO SAVE TO THE DATA LOGGER
			}*/
		}

		//=====================================================================
		// TIMER EVENT METHOD (OnTimer)
		//=====================================================================
		/// <summary>
		/// Called every time the timer goes off.  Determines whether to update relay status and/or pass on sensor data
		/// </summary>
		/// <param name="dataObj">Not sure</param>
		private static void OnTimer(Object dataObj) {
			// Determine what to evaluate and send by XBee, depending on thermostat status and type of loop
			controlLoops++;	// Increment the loop counter
			if(thermoOn) EvaluateProgramming(controlLoops == SENSOR_PERIODS);	// Thermostat is on, so evaluate the relay status through programming rules, only force XBee data if a sensor loop
			else if(controlLoops == SENSOR_PERIODS) SendSensorData(TEMP_UNDEFINED);	// Thermostat is off, so only send XBee data if a sensor loop

			// Reset the counter, if needed
			if(controlLoops == SENSOR_PERIODS) controlLoops = 0;
		}

		//=====================================================================
		// EvaluateProgramming
		//=====================================================================
		/// <summary>
		/// Based on the current day, time and temperature, determine the relay status, and then if sensor data is to be sent to the logger
		/// </summary>
		/// <param name="forceUpdate">Force a sensor data update</param>
		private static void EvaluateProgramming(bool forceUpdate) {
			//-----------------------------------------------------------------
			// COLLECT CONTROL CONDITIONS
			//-----------------------------------------------------------------
			// Get the tempeature reading
			double temperature = tempSensor.readTemperature();

			// Get the time and weekday for evaluating the rules
			double curTime = DateTime.Now.Hour + DateTime.Now.Minute/60.0 + DateTime.Now.Second/3600.0;
			RuleDays curWeekday = (RuleDays) ((int) DateTime.Now.DayOfWeek);	// Cast the returned DayOfWeek enum into the custome DayType enum
			Debug.Print("Evaluating relay status on a " + curWeekday + " (" + DateTime.Now.ToString("dddd") + ") at " + curTime.ToString("F4") + " (" + DateTime.Now.ToString() + ") with measured temperature at " + temperature.ToString("F") + ": ");

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
			} else if(overrideOn) {	// Override is on
				//-------------------------------------------------------------
				// EVALUATE RELAY STATUS AGAINST OVERRIDE TEMPERATURE
				//-------------------------------------------------------------
				if(relayOn && (temperature > (overrideTemp + tempBuffer))) {
					// Turn off relay
					SetRelay(false);
					updatePacket = true;
					Debug.Print("\tOVERRIDE MODE: Relay turned OFF since temperature (" + temperature.ToString("F") + ") is greater than unbuffered override temperature (" + overrideTemp.ToString("F") + ")");
				} else if(!relayOn && (temperature < (overrideTemp - tempBuffer))) {
					// Turn on relay
					SetRelay(true);
					updatePacket = true;
					Debug.Print("\tOVERRIDE MODE: Relay turned ON since temperature (" + temperature.ToString("F") + ") is less than unbuffered override temperature (" + overrideTemp.ToString("F") + ")");
				} else Debug.Print("\tOVERRIDE MODE: Relay remains " + (relayOn ? "ON" : "OFF"));
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
								Debug.Print("\tRelay turned OFF since temperature (" + temperature.ToString("F") + ") is greater than the unbuffered rule temperature (" + curRule.Temperature.ToString("F") + ")");
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
			if(updatePacket) SendSensorData(temperature);
		}

		//=====================================================================
		// RuleApplies
		//=====================================================================
		/// <summary>
		/// Checks to see if the day and time match the rule
		/// </summary>
		/// <param name="rule">Rule to evaluate</param>
		/// <param name="checkDay">Current day</param>
		/// <param name="checkTime">Current time</param>
		/// <returns>Whether the rule is in effect for the day and time</returns>
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
		// ByteToFloat
		//=====================================================================
		/// <summary>
		/// Converts a 4-byte array into a single precision floating point value
		/// </summary>
		/// <param name="byte_array">The byte array to convert</param>
		/// <returns>The float value of the byte array</returns>
		private static unsafe float ByteToFloat(byte[] byte_array) {
			uint ret = (uint) (byte_array[0] << 0 | byte_array[1] << 8 | byte_array[2] << 16 | byte_array[3] << 24);
			float r = *((float*) &ret);
			return r;
		}

		//=====================================================================
		// FloatToByte
		//=====================================================================
		/// <summary>
		/// Convert a float into a 4-byte array
		/// </summary>
		/// <param name="value">The float to convert</param>
		/// <returns>The 4-byte representation of the float</returns>
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

		//=====================================================================
		// SetPowerMode
		//=====================================================================
		/// <summary>
		/// Sets the power mode of the thermostat control
		/// </summary>
		/// <param name="turnOn">Indicates if the thermostat should be turned on</param>
		private static void SetPowerMode(bool turnOn) {
			if(turnOn) {
				// Update the thermostat status indicators
				thermoOn = true;	// Set the master flag
				pwrStatusOutput.Write(true);	// Turn on the thermostat status LED
				Debug.Print("Thermostat turned ON");

				// Determine the relay status
				SetRelay(false);	// Turn off the relay by default as the programming logic will evaluate its status
				EvaluateProgramming(true);	// Force a data update since the thermostat status changed
			} else {
				// Update the thermostat status indicators
				thermoOn = false;	// Set the master flag
				pwrStatusOutput.Write(false);	// Turn off the thermostat status LED
				Debug.Print("Thermostat turned OFF");

				// Open the relay for external control
				SetRelay(true);	// Open the relay
				SendSensorData(TEMP_UNDEFINED);	// Programming rules don't apply, but still need to send data update for thermostat and relay status change
			}
		}

		//=====================================================================
		// FormatApiMode
		//=====================================================================
		/// <summary>
		/// Formats escape characters in the XBee payload data
		/// </summary>
		/// <param name="packet">The payload data</param>
		/// <param name="filterIncoming">Payload from an incoming tranmission with escape characters</param>
		/// <returns>The filtered payload data</returns>
		private static byte[] FormatApiMode(byte[] packet, bool filterIncoming) {
			// Local variables and constants
			byte[] escapeChars = { 0x7d, 0x7e, 0x11, 0x13 };	// The bytes requiring escaping
			const byte filter = 0x20;	// The XOR filter
			byte[] output;	// Contains the formatted packet
			int outSize = packet.Length;	// Contains the size of the outgoing packet

			if(filterIncoming) {	// Removed any escaping sequences
				//-------------------------------------------------------------
				// REMOVE ESCAPING CHARACTERS FROM PACKET FROM XBEE
				//-------------------------------------------------------------
				// Count the outgoing packet size
				foreach(byte b in packet) if(b == escapeChars[0]) outSize--;

				// Iterate through each byte and adjust
				output = new byte[outSize];
				int pos = 0;
				for(int i = 0; i < packet.Length; i++) {
					if(packet[i] == escapeChars[0]) output[pos++] = (byte) (packet[++i]^filter);	// Cast needed as XOR works on ints
					else output[pos++] = packet[i];
				}
			} else {
				//-------------------------------------------------------------
				// ADD ESCAPING CHARACTERS TO PACKET SENT FROM XBEE
				//-------------------------------------------------------------
				// Determine the new size
				foreach(byte b in packet) if(Array.IndexOf(escapeChars, b) > -1) outSize++;

				// Iterate through each byte and adjust
				output = new byte[outSize];
				int pos = 0;
				for(int i = 0; i < packet.Length; i++) {
					if(Array.IndexOf(escapeChars, packet[i]) > -1) {
						output[pos++] = escapeChars[0];
						output[pos++] = (byte) (packet[i]^filter);
					} else output[pos++] = packet[i];
				}
			}

			return output;
		}

		//=====================================================================
		// SetRelay
		//=====================================================================
		/// <summary>
		/// Operate the relay
		/// </summary>
		/// <param name="openRelay">Turn on the relay</param>
		private static void SetRelay(bool openRelay) {
			if(openRelay && !relayOn) {	// Turn on relay only when it's off
				relayOn = true;	// Set master flag
				relayStatusOutput.Write(true);	// Code only for testing - just turns on LED
			} else if(!openRelay && relayOn) {
				relayOn = false;	// Set master flag
				relayStatusOutput.Write(false);	// Code only for testing - just turns off LED
			}
		}
	}
}
