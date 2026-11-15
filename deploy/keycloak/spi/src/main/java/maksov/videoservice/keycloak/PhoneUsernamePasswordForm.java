package maksov.videoservice.keycloak;

import jakarta.ws.rs.core.MultivaluedMap;
import java.util.List;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.authenticators.browser.UsernamePasswordForm;
import org.keycloak.models.UserModel;
import org.keycloak.services.managers.AuthenticationManager;

/**
 * "Username + password" form that accepts a username, e-mail OR phone number.
 * If the input looks like a phone number and exactly one user with that number exists,
 * we substitute their username and the standard password check, brute-force protection and OTP take over.
 * If the user is not found or the number is ambiguous, the input is unchanged and the response is the same as for a wrong username
 * (no number enumeration).
 */
public class PhoneUsernamePasswordForm extends UsernamePasswordForm {

    @Override
    protected boolean validateForm(AuthenticationFlowContext context, MultivaluedMap<String, String> formData) {
        PhoneNumbers.normalize(formData.getFirst(AuthenticationManager.FORM_USERNAME)).ifPresent(phone -> {
            List<UserModel> matches = context.getSession().users()
                    .searchForUserByUserAttributeStream(context.getRealm(), PhoneNumbers.ATTRIBUTE, phone)
                    .limit(2)
                    .toList();
            if (matches.size() == 1) {
                formData.putSingle(AuthenticationManager.FORM_USERNAME, matches.get(0).getUsername());
            }
        });
        return super.validateForm(context, formData);
    }
}

