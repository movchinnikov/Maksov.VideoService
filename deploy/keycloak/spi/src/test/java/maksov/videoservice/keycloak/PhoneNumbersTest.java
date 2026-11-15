package maksov.videoservice.keycloak;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Optional;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.NullAndEmptySource;
import org.junit.jupiter.params.provider.ValueSource;

class PhoneNumbersTest {

    @ParameterizedTest
    @CsvSource(delimiter = '|', value = {
            "+79991234567|+79991234567",
            "+7 (999) 123-45-67|+79991234567",
            "89991234567|+79991234567",
            "8 999 123 45 67|+79991234567",
            "79991234567|+79991234567",
            "+1 415 555 2671|+14155552671",
            "  +44.20.7946.0958  |+442079460958",
    })
    void normalizesPhones(String input, String expected) {
        assertEquals(Optional.of(expected), PhoneNumbers.normalize(input));
    }

    @ParameterizedTest
    @NullAndEmptySource
    @ValueSource(strings = {"demo", "demo@videoservice.local", "user123", "+", "+0123456789", "12345", "1234567890123456", "+7 999 abc"})
    void ignoresNonPhones(String input) {
        assertTrue(PhoneNumbers.normalize(input).isEmpty());
    }

    @Test
    void e164PatternMatchesStoredFormat() {
        assertTrue(PhoneNumbers.E164.matcher("+79991234567").matches());
        assertTrue(!PhoneNumbers.E164.matcher("89991234567").matches());
    }
}

