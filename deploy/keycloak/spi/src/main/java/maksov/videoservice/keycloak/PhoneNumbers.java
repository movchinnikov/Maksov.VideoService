package maksov.videoservice.keycloak;

import java.util.Optional;
import java.util.regex.Pattern;

/** Normalizes a phone number entered by the user to E.164 (+79991234567). */
public final class PhoneNumbers {

    /** Name of the user attribute that stores the normalized number. */
    public static final String ATTRIBUTE = "phoneNumber";

    /** Strict storage format: "+", country code without a leading zero, at most 15 digits in total. */
    public static final Pattern E164 = Pattern.compile("^\\+[1-9][0-9]{9,14}$");

    private static final Pattern FORMATTING = Pattern.compile("[\\s().\\-]");
    private static final Pattern DIGITS = Pattern.compile("^[0-9]{7,15}$");

    private PhoneNumbers() {
    }

    /**
     * Normalizes the input. Returns empty if it does not look like a phone number
     * (e.g. a regular username with letters), in which case sign-in proceeds as by username/e-mail.
     * <ul>
     *   <li>spaces, parentheses, dots and hyphens are removed;</li>
     *   <li>"8XXXXXXXXXX" (11 digits) → "+7XXXXXXXXXX" (Russian trunk prefix "8");</li>
     *   <li>without "+", other numbers are assumed to include the country code: "79991234567" → "+79991234567".</li>
     * </ul>
     */
    public static Optional<String> normalize(String raw) {
        if (raw == null) {
            return Optional.empty();
        }
        String s = FORMATTING.matcher(raw.strip()).replaceAll("");
        boolean hasPlus = s.startsWith("+");
        String digits = hasPlus ? s.substring(1) : s;
        if (!DIGITS.matcher(digits).matches()) {
            return Optional.empty();
        }
        if (!hasPlus && digits.length() == 11 && digits.charAt(0) == '8') {
            digits = "7" + digits.substring(1);
        }
        String e164 = "+" + digits;
        return E164.matcher(e164).matches() ? Optional.of(e164) : Optional.empty();
    }
}

