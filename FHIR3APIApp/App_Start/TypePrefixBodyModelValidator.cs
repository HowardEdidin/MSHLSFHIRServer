using System;
using System.Web.Http.Controllers;
using System.Web.Http.Metadata;
using System.Web.Http.Validation;

namespace FHIR4APIApp
{
    public class TypePrefixBodyModelValidator : IBodyModelValidator
    {
        private readonly IBodyModelValidator innerValidator;

        public TypePrefixBodyModelValidator(IBodyModelValidator innerValidator)
        {
	        this.innerValidator = innerValidator ?? throw new ArgumentNullException(nameof(innerValidator));
        }

        public bool Validate(object model, Type type, ModelMetadataProvider metadataProvider, HttpActionContext actionContext, string keyPrefix)
        {
            // Remove the keyPrefix but otherwise let innerValidator do what it normally does.
            return innerValidator.Validate(model, type, metadataProvider, actionContext, "bla ba");
        }
    }
}