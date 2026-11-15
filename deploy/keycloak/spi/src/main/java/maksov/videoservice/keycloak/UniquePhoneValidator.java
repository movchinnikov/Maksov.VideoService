package maksov.videoservice.keycloak;

import java.util.Collection;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.UserModel;
import org.keycloak.userprofile.UserProfileAttributeValidationContext;
import org.keycloak.validate.SimpleValidator;
import org.keycloak.validate.ValidationContext;
import org.keycloak.validate.ValidationError;
import org.keycloak.validate.ValidatorConfig;

/** Profile attribute validator: the phone number must not belong to another user. */
public class UniquePhoneValidator implements SimpleValidator {

    public static final String ID = "phone-unique";
    public static final String MESSAGE = "phoneNumberTaken";

    @Override
    public String getId() {
        return ID;
    }

    @Override
    public ValidationContext validate(Object input, String inputHint, ValidationContext context, ValidatorConfig config) {
        Collection<?> values = input instanceof Collection<?> c ? c : input == null ? java.util.List.of() : java.util.List.of(input);
        var attributeContext = UserProfileAttributeValidationContext.from(context).getAttributeContext();
        KeycloakSession session = attributeContext.getSession();
        UserModel current = attributeContext.getUser();

        for (Object value : values) {
            String phone = value == null ? "" : value.toString().strip();
            if (phone.isEmpty()) {
                continue;
            }
            boolean takenByAnother = session.users()
                    .searchForUserByUserAttributeStream(session.getContext().getRealm(), PhoneNumbers.ATTRIBUTE, phone)
                    .anyMatch(u -> current == null || !u.getId().equals(current.getId()));
            if (takenByAnother) {
                context.addError(new ValidationError(ID, inputHint, MESSAGE));
            }
        }
        return context;
    }
}

