Unless requested, run tests without end-to-end tests with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`. When you finished to implement a stage or a substential modification, run all tests and ensure they are all passing (including end-to-end tests). In case of failure, fix them.

In service constructors, avoid using optional parameters. You should require all services you need in order to work properly. Avoid trying to have a fallback strategy if a service is not available. If a service is not available, it's better to let the application fail fast rather than silently using a default value that might not be appropriate for the use case. For tests use mocks (with Moq), fakes or stubs when needed.

No silent failures: bare `catch { }` blocks without logging or justification are forbidden. Real failures must fail fast with dedicated domain exceptions.

Clean break policy : we are still in early developpement stage. Do not try to have backward compatibility. Do not hesitate to break anything if it helps us go faster. Do not hesitate to propose new architectural patterns or to refactor existing code if it helps us go faster. Do not hesitate to question existing decisions and to propose new ones. This is a fast moving project and we need to be agile.

Avoid magic number and strings. Use constants or enums. This avoid bugs and make code easier to maintain.

Avoid creating synthetic values for something that should exists in database, make a hard error. For instance, if a workflow is not found, throw an exception. Do not create a default workflow on the fly. Instead, return the proper exception so that the UI can display an appropriate message.

Planning documents (docs/plan) are **specifications**: they state required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.
