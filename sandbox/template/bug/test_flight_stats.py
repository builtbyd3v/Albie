from flight_stats import apogee, average_climb_rate, time_to_apogee

TIMES = [0.0, 1.0, 2.0, 3.0, 4.0]
ALTITUDES = [0.0, 40.0, 70.0, 80.0, 60.0]


def test_apogee():
    assert apogee(ALTITUDES) == 80.0


def test_time_to_apogee():
    assert time_to_apogee(TIMES, ALTITUDES) == 3.0


def test_average_climb_rate():
    assert abs(average_climb_rate(TIMES, ALTITUDES) - 80.0 / 3.0) < 1e-9
