using System;
using Microsoft.SPOT;

namespace RelayControllerTest {

	class TemperatureRule {
		// Enums
		public enum DayType { Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Weekdays, Weekends, Everyday };

		// Members
		private DayType _days;
		private double _time;
		private double _temperature;

		// Constructor
		public TemperatureRule(DayType Day, double Time, double Temperature) {
			_days = Day;
			_time = Time;
			_temperature = Temperature;
		}
	}
}
