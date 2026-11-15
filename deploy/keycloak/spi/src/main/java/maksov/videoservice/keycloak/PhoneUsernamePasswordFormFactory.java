package maksov.videoservice.keycloak;

import org.keycloak.authentication.Authenticator;
import org.keycloak.authentication.authenticators.browser.UsernamePasswordFormFactory;
import org.keycloak.models.KeycloakSession;

public class PhoneUsernamePasswordFormFactory extends UsernamePasswordFormFactory {

    public static final String ID = "phone-username-password-form";

    @Override
    public String getId() {
        return ID;
    }

    @Override
    public Authenticator create(KeycloakSession session) {
        return new PhoneUsernamePasswordForm();
    }

    @Override
    public String getDisplayType() {
        return "Username, e-mail or phone + password form";
    }

    @Override
    public String getHelpText() {
        return "Sign in by username, e-mail or phone number and password.";
    }
}

