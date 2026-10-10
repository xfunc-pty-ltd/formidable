# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [2.0.0] - 2026-10-10

### Breaking changes

- *(core)* Rules nested in a child validator written for a base type, and the rows of RuleFor(x => x.Items).ForEach(...), are read for the required mark. Such fields gain the mark and aria-required where FluentValidation requires them, and a row-filtered presence rule there answers ConditionallyRequired. GetDeclaredFieldPaths lists Items[].Sku for those rows, where it listed Items.Sku for an IEnumerable<T> property and nothing for a list or an array. Code that matches declared paths should look for Items[].Sku, and a RequiredOverride added only to supply the missing mark can be removed. ([5507a90](https://github.com/xfunc-pty-ltd/formidable/commit/5507a909a51a2a82730d87a2b9b79717091dd9f8))
- *(core)* A per-row rule such as RuleForEach(x => x.Tags).NotEmpty() leaves the list's own component unmarked, and a load leaves that component unconfirmed. GetDeclaredFieldPaths lists Tags[] instead of Tags, and GetFieldRequirement answers for Tags[0], not for Tags. Code that read the rule's requirement or declared path on the list itself reads it on the rows. ([d15805b](https://github.com/xfunc-pty-ltd/formidable/commit/d15805b41baaa142846ebee86c38abb78bb5a4de))
- *(blazor)* ToFieldIdentifier returns (list, "0") for a list or array element, the identity Blazor gives a row bound with () => Model.Tags[i]. It returned (list, "[0]"). Code that looks up such a row's state or issues by (list, "[0]") passes (list, "0"), or FieldIdentifier.Create(() => Model.Tags[0]). ([e979dfa](https://github.com/xfunc-pty-ltd/formidable/commit/e979dfa77a2a423ea90be732c8ada5c41ea613a6))
- *(core)* A NotEmpty() or NotNull() rule that fails as a warning or info draws no required mark or aria-required, and GetFieldRequirement answers NotRequired for it, where 1.x graded it as required. A presence rule whose severity is decided from the model, such as WithSeverity(x => ...), reads as ConditionallyRequired and draws no mark. With ValidatorOptions.Global.Severity set to Warning or Info, a presence rule without WithSeverity follows it and draws no mark. To keep the mark on such a field, write the rule with WithSeverity(Severity.Error) or declare the field through RequiredOverride. ([4af751e](https://github.com/xfunc-pty-ltd/formidable/commit/4af751e1f38e89b2c3d05983ffd720240e540069))
- *(aspnetcore)* A project that runs the AOT analyser gets IL3050 at each use of [Validate] (class, action or new ValidateAttribute()), worded "Using member 'Formidable.AspNetCore.ValidateAttribute.ValidateAttribute()' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling" and the reason. [Validate(typeof(Order), ...)] names ValidateAttribute.ValidateAttribute(params Type[]) instead. Under TreatWarningsAsErrors each is an error. EnableAotAnalyzer, PublishAot and IsAotCompatible turn the analyser on; without them, nothing changes. To keep [Validate] in an app that never publishes with Native AOT, suppress the analyser at those sites with #pragma warning disable IL3050. The pragma does not quiet the native compiler's IL3050 at a reachable new ValidateAttribute(). To publish with Native AOT, use Validate<TModel>() on minimal APIs. ([dfa3492](https://github.com/xfunc-pty-ltd/formidable/commit/dfa3492c2edff1a5f89a751be14a9ac52e21d373))

### Added

- *(blazor)* Say once why IsFormValid is untracked ([641e6d7](https://github.com/xfunc-pty-ltd/formidable/commit/641e6d7d7bb82505a401e2721f40277c0967cc90))
- *(core)* Trace a profile that selects no rule of a validator ([ec46d3d](https://github.com/xfunc-pty-ltd/formidable/commit/ec46d3d7669394f762ff922c23ff44534d486fc9))
- *(core)* Mark the core package AOT-compatible ([5ec000a](https://github.com/xfunc-pty-ltd/formidable/commit/5ec000a2c107b3daeee57b4070835a0f4c7b34d7))
- *(blazor)* Add and remove collection rows in one call ([6403937](https://github.com/xfunc-pty-ltd/formidable/commit/6403937ed83289a853b49c8137cc1a40fe028117))
- *(blazor)* Run a field's edit and report it with Edit ([ff15277](https://github.com/xfunc-pty-ltd/formidable/commit/ff15277edd353ac87d2c8099117ceacb8e7eb16d))
- *(blazor)* Name every visible issue for templates to read ([27f22b3](https://github.com/xfunc-pty-ltd/formidable/commit/27f22b36c0324f2d99cc818ce5d79436f09af603))
- *(blazor)* Template each item of the inline message lists ([bd00ca4](https://github.com/xfunc-pty-ltd/formidable/commit/bd00ca46584ef0f35614571de2d28bdf5ac432e7))
- *(blazor)* Splat attributes onto the required marker ([3a20325](https://github.com/xfunc-pty-ltd/formidable/commit/3a2032568cee58a45b1e3713bcac47da0b1366de))
- *(blazor)* Wait for the first submit with a WaitForSubmit parameter ([cb4b4c3](https://github.com/xfunc-pty-ltd/formidable/commit/cb4b4c374d0dfe9f2194deea1102c4e5ea99a98f))
- *(sample)* Fit MudBlazor inputs through FormidableField ([4283920](https://github.com/xfunc-pty-ltd/formidable/commit/42839209063f54d06739c9c75587701417a8b482))

### Fixed

- *(blazor)* Check validity after a change when the window never closes ([5aa344d](https://github.com/xfunc-pty-ltd/formidable/commit/5aa344d55f7feb5e38ed6960614153cee9b17b3d))
- *(blazor)* Warn at form build about a debounce the timer cannot arm ([cdff8d1](https://github.com/xfunc-pty-ltd/formidable/commit/cdff8d1769d72d2967c42e0a54fce5056cf11537))
- *(core)* Report a Must overlap only when its message repeats ([840e4cb](https://github.com/xfunc-pty-ltd/formidable/commit/840e4cb685edad089969b2aa2d0279e824a6cbe8))
- *(blazor)* Load enum and struct values under Native AOT ([d38b040](https://github.com/xfunc-pty-ltd/formidable/commit/d38b040c7a729e52ac574fb65a9f428eecd3331b))
- *(blazor)* Name an issue by its path when its display name is empty ([8e9224f](https://github.com/xfunc-pty-ltd/formidable/commit/8e9224fd61d14f2b825544d256f4f7a578715910))
- *(blazor)* Key summary entries and message items on their issue ([44d4fb4](https://github.com/xfunc-pty-ltd/formidable/commit/44d4fb459eb3e8c2c5a22a268cbf77cbfbb6c6d1))
- *(blazor)* Reconcile the form when a nested render changes its fields ([1e62dc6](https://github.com/xfunc-pty-ltd/formidable/commit/1e62dc64b977f54bead6dd9e7cf1f85381731588))
- Name a nullable type by the type it holds in diagnostics ([1fd2b7f](https://github.com/xfunc-pty-ltd/formidable/commit/1fd2b7faa4c799f6225dfd7c4a31b9bac035d9a7))
- *(blazor)* Retry a field-set reconcile whose handler threw ([4b1a96c](https://github.com/xfunc-pty-ltd/formidable/commit/4b1a96cf2d0c530cc490214e0db520f0aa3241b7))
- *(blazor)* Read a debounce the timer cannot take as never or zero ([1cdff06](https://github.com/xfunc-pty-ltd/formidable/commit/1cdff061af9b27e98ca0716160abcd90da494400))
- *(blazor)* Stop a zero debounce spinning while a check is out ([2040292](https://github.com/xfunc-pty-ltd/formidable/commit/20402921b7cbbb3edb0d2e1ea70044031a412d8e))
- *(blazor)* Keep IsFormValid on the latest edit after a load or submit ([5b25583](https://github.com/xfunc-pty-ltd/formidable/commit/5b25583e2fcb52a4396c79426d6b2de6e56061fe))
- *(blazor)* Honour a cancelled token before a check's answer lands ([12b3d23](https://github.com/xfunc-pty-ltd/formidable/commit/12b3d2394531aa198038bf7931babbe211cee2bf))
- *(blazor)* Ignore calls on a disposed form or validator ([9d919c6](https://github.com/xfunc-pty-ltd/formidable/commit/9d919c6d4bbe56c7e7acc934e2a3e4b363282ab0))
- *(blazor)* Skip a load's live check once its engine is disposed ([8b798c1](https://github.com/xfunc-pty-ltd/formidable/commit/8b798c18c3250baa241befb8584a32bdbd0ea4ca))
- *(blazor)* Name the loop key when index-bound rows diverge ([35dc908](https://github.com/xfunc-pty-ltd/formidable/commit/35dc908128aa511bc0983a03235a07176c1f553d))
- *(core)* End the rule walk at a factory that builds its own type ([70cf391](https://github.com/xfunc-pty-ltd/formidable/commit/70cf391a7a7dc3f6335149284ad1236fe4cfdb1b))
- *(blazor)* Never mark a dictionary entry as passing submit ([b5bc50b](https://github.com/xfunc-pty-ltd/formidable/commit/b5bc50b98bf8a3c0662313a4fe2add6906428d6c))
- *(blazor)* Show the form's message when each shown error is off screen ([87d8532](https://github.com/xfunc-pty-ltd/formidable/commit/87d85320872cf95be1de002bf61fcd87c5793972))
- *(blazor)* Send a posted callback's throw to the error boundary ([ea0843d](https://github.com/xfunc-pty-ltd/formidable/commit/ea0843d00fdc6d90b9ee448f2eb8b475fc8ae59a))

### Documentation

- State the 1.x security support posture ([b673d47](https://github.com/xfunc-pty-ltd/formidable/commit/b673d47f1627e6159088e8b02515571678dd304d))
- State the security support policy without a major number ([6dba46a](https://github.com/xfunc-pty-ltd/formidable/commit/6dba46ad545fdeb5396c34128c11d6374de4a82e))
- Check row keys while the tutorial runs in Development ([014def2](https://github.com/xfunc-pty-ltd/formidable/commit/014def21ea70be796a3995727011e2524e1dd369))
- Answer the debounce, draft-save and server re-check questions ([40f5ea1](https://github.com/xfunc-pty-ltd/formidable/commit/40f5ea1b16a812c5564a6f8b990fa6993b31da04))
- Keep a focused field clear of a sticky header ([a3d48aa](https://github.com/xfunc-pty-ltd/formidable/commit/a3d48aa6d32c9f547933b6d654b0b2a8e3d981e7))
- Troubleshoot a FluentValidation validator passed to Validator ([d3f3823](https://github.com/xfunc-pty-ltd/formidable/commit/d3f382334ccb1c9dad8d3125f4987ce8a4689dfc))
- Label the sample page under each option that has one ([0983567](https://github.com/xfunc-pty-ltd/formidable/commit/0983567db97afdc80efe1b31874bfcee32cc4028))
- Retitle stage 3's tier section and drop a one-off noun ([a199c49](https://github.com/xfunc-pty-ltd/formidable/commit/a199c49988acfe63e7b8dced046676669e89d1dd))
- Say a draft save gated on the error count refuses a warning ([aac0dc2](https://github.com/xfunc-pty-ltd/formidable/commit/aac0dc2786b8ef7774c447995205968ffe13a71d))
- *(sample)* Spell out the checklist's padded whitespace values ([f31536f](https://github.com/xfunc-pty-ltd/formidable/commit/f31536f3bae40a130a349cd9af69c014e4053fbe))
- *(sample)* Tidy three teaching bullets and name the fade coupling ([4115a9f](https://github.com/xfunc-pty-ltd/formidable/commit/4115a9fd461ae519225e0eaa51c7514ba6c75629))
- Quote the three recording test doubles in testing ([b7800eb](https://github.com/xfunc-pty-ltd/formidable/commit/b7800ebf4d91be8821197f65f4ebe54ee73921c0))
- Give the summary's unresolved order one name ([098fb45](https://github.com/xfunc-pty-ltd/formidable/commit/098fb45bbf05039ba7420fe010d980122c917426))
- Say a dictionary entry draws no required mark ([a514e0f](https://github.com/xfunc-pty-ltd/formidable/commit/a514e0f93b77b187ccd5589381a0dd9325022778))
- Say which server issues the on-page check still judges ([6034c3d](https://github.com/xfunc-pty-ltd/formidable/commit/6034c3d63ecf43d42833791d95c30dae22828963))
- Recipe for a list that needs at least N items ([561ae6c](https://github.com/xfunc-pty-ltd/formidable/commit/561ae6c05aac706bf2099bb1123446d190ea65c9))
- Name every CI job and correct the between-tags version ([6d6c262](https://github.com/xfunc-pty-ltd/formidable/commit/6d6c26296e4037c1b34dd1f97fcfb812126ecd56))
- Name the LiveDebounce window that never closes ([3dc0de6](https://github.com/xfunc-pty-ltd/formidable/commit/3dc0de601baf06bb7a329f1039e7942cc4062ccb))
- State the trimming and AOT posture of each package ([5ec04f3](https://github.com/xfunc-pty-ltd/formidable/commit/5ec04f3716ee8d9e9c994ada153d888360ed9958))
- *(sample)* Add and remove /workout attendees through the context ([e2b8d9d](https://github.com/xfunc-pty-ltd/formidable/commit/e2b8d9d9f0da8571755a60a1f43e1cba6dcf4379))
- Scope the custom-summary recipe's name fallback to text ([6251984](https://github.com/xfunc-pty-ltd/formidable/commit/6251984944d51c254b7f81306495140a828722cf))
- Name an entry by its path only where the path has text ([44629b8](https://github.com/xfunc-pty-ltd/formidable/commit/44629b8029fb0347106094a441eff4264d001025))
- *(sample)* Name FluentValidation's own name in the entry name rule ([905151c](https://github.com/xfunc-pty-ltd/formidable/commit/905151cb1bdd51def83d18afc3b5b8b02705d635))
- *(core)* Say where an issue's display name comes from ([1899223](https://github.com/xfunc-pty-ltd/formidable/commit/1899223e5baa6237463571697493bb31266c93f8))
- Scope the row-object promise to rows that are objects ([9c3f0cc](https://github.com/xfunc-pty-ltd/formidable/commit/9c3f0cc8466dd998c5144f5b39883490134bcd34))
- Add a reference page for the engine and its results ([154bdd2](https://github.com/xfunc-pty-ltd/formidable/commit/154bdd2e5c6878d380f6fadde712887c762183ce))
- *(sample)* Place the required mark on every sample page ([a4747cc](https://github.com/xfunc-pty-ltd/formidable/commit/a4747cc450ce742b1392048dc109b044d643fc8f))
- *(sample)* Render aria-required on the vanilla page's native input ([7a03bf5](https://github.com/xfunc-pty-ltd/formidable/commit/7a03bf508c81fa465e9b9b6d86dc308ea77c242a))
- *(sample)* Lead the profile and disclosure pages with steps and rules ([a8761af](https://github.com/xfunc-pty-ltd/formidable/commit/a8761af8e71be3ab937c6081c6c5004e3aef959d))
- *(sample)* Lead the field and collection pages with steps and rules ([aa29fb4](https://github.com/xfunc-pty-ltd/formidable/commit/aa29fb40732540b46eb87ad7341edb225a5b34b8))
- *(sample)* Lead the submit and summary pages with steps and rules ([d55bf1d](https://github.com/xfunc-pty-ltd/formidable/commit/d55bf1da566c478f804631e70dda04a1ed422658))
- *(sample)* Lead the UI and model pages with steps and rules ([f5fba90](https://github.com/xfunc-pty-ltd/formidable/commit/f5fba9000dbadc6f4fcdc30f5e74a6ce06ea5d92))
- *(sample)* Turn the full workout page into a guided tour ([cd0835e](https://github.com/xfunc-pty-ltd/formidable/commit/cd0835e2c6a5a9dd722ada8e1a9224eb4fdb6bc8))
- *(sample)* Set the disclosure page's second form apart ([3a10ef1](https://github.com/xfunc-pty-ltd/formidable/commit/3a10ef1f6c311992296835d6276d81a60f44de9d))
- *(sample)* Follow each page's steps in the release checklist ([9175b6a](https://github.com/xfunc-pty-ltd/formidable/commit/9175b6accf1fa9bde24bcf9369be2504158f68b6))
- Say Enter sends nothing on the server sample ([310a259](https://github.com/xfunc-pty-ltd/formidable/commit/310a25952c07731b3bc0edc8357a00083a4ff612))
- Say what the server sample shows before a submit in two recipes ([6781939](https://github.com/xfunc-pty-ltd/formidable/commit/6781939fac0d12ef36bbdf68dcbb4798b88d8111))
- Give the server sample's options entry both of its conditions ([b3d8773](https://github.com/xfunc-pty-ltd/formidable/commit/b3d87735edda4ac6ad61eb668807fd4582034c92))
- Say which rule moves take the required mark with them ([56b7b73](https://github.com/xfunc-pty-ltd/formidable/commit/56b7b73e4c198b985e0964ed3c823195ea138a28))
- List the browser tests that span pages ([04620bf](https://github.com/xfunc-pty-ltd/formidable/commit/04620bf3a04c60b02d8eb0e7a7ea012bfe09247d))
- Key a loop of index-bound rows by the list it renders ([373de96](https://github.com/xfunc-pty-ltd/formidable/commit/373de967c67bdaf49c00a938856e4df6dcef8def))
- Say a waiting field keeps a loaded message back once it renders ([49b3077](https://github.com/xfunc-pty-ltd/formidable/commit/49b30775ea61aae2519602941cdc8a894d812259))
- Say which fields the re-check answers after a blocked submit ([5b78148](https://github.com/xfunc-pty-ltd/formidable/commit/5b78148825b20d75392ecb234add3b0fc9bee345))
- *(blazor)* Say GetIssues can list one message twice ([4880fcd](https://github.com/xfunc-pty-ltd/formidable/commit/4880fcdba65662f6dd1ccd8df2ec59f3744ec158))
- Give the summary recipe's class its namespace ([7c876a3](https://github.com/xfunc-pty-ltd/formidable/commit/7c876a38d1bce6e1af2e93f030ec1324a8ae7427))
- Say what the kit page's unset template renders ([093dd2a](https://github.com/xfunc-pty-ltd/formidable/commit/093dd2af45224a3aa3b6a0b5b5f3691594fc0b2e))
- *(sample)* Drop process notes from the checklist and the project file ([28001be](https://github.com/xfunc-pty-ltd/formidable/commit/28001bec56d1c94bbe39cfb1edb5440f30a1229d))
- Say a server reply also puts its fields under watch ([3967ef9](https://github.com/xfunc-pty-ltd/formidable/commit/3967ef9739b4cb3b920f23ddcf37234696aabdc9))
- Turn six sample-line em dashes into colons on the options page ([25c5904](https://github.com/xfunc-pty-ltd/formidable/commit/25c590431f7ca4d93bfc85d416104b4c48d19922))
- *(blazor)* Say which fields off the page still show a submit error ([de7a874](https://github.com/xfunc-pty-ltd/formidable/commit/de7a874261d1c7f6a2f5cf1921c24431d69307f0))
- Scope the dictionary green claim to an input bound to the entry ([4a9494e](https://github.com/xfunc-pty-ltd/formidable/commit/4a9494e68df3d9b2b01a4493ac0f91a263761592))
- Say where a missing body never reaches the filter ([3ab1082](https://github.com/xfunc-pty-ltd/formidable/commit/3ab108220e88ffcaf946bb967db7cf3c833a9f54))
- Mark the tutorial's required fields from stage 2 on ([0b20ff4](https://github.com/xfunc-pty-ltd/formidable/commit/0b20ff4c624b6c73254be584f5ab8dd562937e8c))
- Require the tutorial's members with NotEmpty() ([5c5a9c1](https://github.com/xfunc-pty-ltd/formidable/commit/5c5a9c1a642daed043d1449ea1ac2d17896ad058))
- Say what the tutorial's row-key check catches ([bb51d2d](https://github.com/xfunc-pty-ltd/formidable/commit/bb51d2ded8bc719321c72db07284576baf9b58aa))
- *(sample)* Split the tutorial server's message at its full stop ([60d8a18](https://github.com/xfunc-pty-ltd/formidable/commit/60d8a184e815f5c2c38dc64ad496c2c133d28604))
- *(sample)* Cover the tutorial's stages in the release checklist ([a06870b](https://github.com/xfunc-pty-ltd/formidable/commit/a06870be6c56b1172d31b1a4e96ba422d38a09e6))
- *(sample)* Shorten the workout page's dietary notes message ([494d5af](https://github.com/xfunc-pty-ltd/formidable/commit/494d5af044c82c09a72df688640a1c92a88962d5))
- *(sample)* Label the custom profiles box Read time (minutes) ([915ecdb](https://github.com/xfunc-pty-ltd/formidable/commit/915ecdb6c97000079285a8af14c1f6b04000a15d))
- Adopt the Contributor Covenant as the code of conduct ([421d0f2](https://github.com/xfunc-pty-ltd/formidable/commit/421d0f25b337c1d5bad26608493a52f573c444f3))
- Say warnings and infos never block a submit ([dd9c76b](https://github.com/xfunc-pty-ltd/formidable/commit/dd9c76b8e9b63e1bc22ddbacc076ccd4a45612fd))
- Retake the tutorial screenshots for stages 2 to 6 ([9092d5e](https://github.com/xfunc-pty-ltd/formidable/commit/9092d5e20d4b391d4c60b47b9711f8a83b1dd5e9))
- Retake the hero images and the social preview ([ed32e4b](https://github.com/xfunc-pty-ltd/formidable/commit/ed32e4b5dbec8b41ae01cae53e3f74705c8c9bf0))
- Describe what the README's hero image shows ([68d1936](https://github.com/xfunc-pty-ltd/formidable/commit/68d193687a9bb3f9af3b4fa41fb5ad3387203bd1))
- Fire bUnit events through InvokeAsync in the testing guide ([4c349bc](https://github.com/xfunc-pty-ltd/formidable/commit/4c349bc08ecd5213fa8c6d5d2283598f59dd517e))

## [1.0.2] - 2026-09-27

### Fixed

- *(blazor)* Let a re-check set IsValidating on fields in LiveDebounce ([36c173a](https://github.com/xfunc-pty-ltd/formidable/commit/36c173ab1a0873c69bdbc773fc7d750f4874893f), [#19](https://github.com/xfunc-pty-ltd/formidable/issues/19))
- *(blazor)* List a mid-submit server reply in VisibleErrorSummary ([47a9ea9](https://github.com/xfunc-pty-ltd/formidable/commit/47a9ea988e15abe4bd811095bc1cf313592a6da8), [#20](https://github.com/xfunc-pty-ltd/formidable/issues/20))

### Documentation

- Bring the release runbook up to the 1.x release shape ([f88e8ff](https://github.com/xfunc-pty-ltd/formidable/commit/f88e8ff2379c7b64e2be2429100d5c85fa277724))
- Split the XML docs paragraph in CONTRIBUTING ([314aa7e](https://github.com/xfunc-pty-ltd/formidable/commit/314aa7e2dc9cc0c8a8bcaabb299de7529fe0431f))

## [1.0.1] - 2026-09-24

### Fixed

- *(blazor)* Keep a server reply until something newer answers ([94f6db4](https://github.com/xfunc-pty-ltd/formidable/commit/94f6db4f56fdab6b2d066e9f0a93f51663619d31), [#18](https://github.com/xfunc-pty-ltd/formidable/issues/18))

### Documentation

- Find a kit field by its id in component tests ([28abd5b](https://github.com/xfunc-pty-ltd/formidable/commit/28abd5b074a4aec00c43b73f63530b6e3ce11428))

## [1.0.0] - 2026-09-22

### Added

- *(core)* Profiles, the report model, and the validator seam ([d3b9e07](https://github.com/xfunc-pty-ltd/formidable/commit/d3b9e07add81ede31ea302d5f2466d209397eeae))
- *(blazor)* The validation engine and the two form roots ([5cb6bc7](https://github.com/xfunc-pty-ltd/formidable/commit/5cb6bc7225bc7505dad8ea91de75e4fe7e6b8637))
- *(blazor)* The headless component kit ([b4b7c86](https://github.com/xfunc-pty-ltd/formidable/commit/b4b7c869aadfb32743aae91474e5e542aff3c0c2))
- *(aspnetcore)* Validation filters and the shared wire contract ([83f00f8](https://github.com/xfunc-pty-ltd/formidable/commit/83f00f8b57feca993da8b1526455a10ed497d91f))
- *(sample)* The sample app, its API, and the first teaching pages ([32812e0](https://github.com/xfunc-pty-ltd/formidable/commit/32812e09da0d3090eaed3d12b8ed9d444a17c0c4))
- *(sample)* The xfunc Soft theme and the styled validation surface ([a86d1e8](https://github.com/xfunc-pty-ltd/formidable/commit/a86d1e8cbcf2a6f7812a0bc75fcc0344531cbd14))
- *(sample)* Teaching panels on every page ([a27d988](https://github.com/xfunc-pty-ltd/formidable/commit/a27d9886eb042425da6b065e0400503c5d8e763d))
- *(sample)* Field state, normalize, and five more pages ([4dba94d](https://github.com/xfunc-pty-ltd/formidable/commit/4dba94dbc26c6409a84ebd55f210d6fa3243f0cb))
- *(sample)* The event-registration workout page ([1e530e2](https://github.com/xfunc-pty-ltd/formidable/commit/1e530e20731b3c028d1d6daffefa2012b5ad88f1))
- *(blazor)* The input base opens as the extension point ([e54aea2](https://github.com/xfunc-pty-ltd/formidable/commit/e54aea27abfc9e58619a60f52f3ed45ea0a1fb75))
- *(blazor)* Own the server round trip and the first wiring mistakes ([5d864c6](https://github.com/xfunc-pty-ltd/formidable/commit/5d864c616ceab47ec17ae774d1ae5655d68ea3ee))
- *(blazor)* Typed number and date inputs on the curated seam ([822bba7](https://github.com/xfunc-pty-ltd/formidable/commit/822bba763831b5d5f271e1b2927d11757e7145bf))
- *(blazor)* LiveDebounce, validity tracking, and the diagnostics ([fd83a2f](https://github.com/xfunc-pty-ltd/formidable/commit/fd83a2fc447f9c7f2309633f97aa3efe8774b835))
- *(blazor)* Order issues by the page, focus the first error ([fe0289f](https://github.com/xfunc-pty-ltd/formidable/commit/fe0289f1b729de12d21b5ce0bcd3f6791cf3dbc9))
- *(core)* Memoize an async rule, run each rule once after a submit ([ff6bcb4](https://github.com/xfunc-pty-ltd/formidable/commit/ff6bcb4d2b3ae7f09a0f0afd82f4b45a4dc006c1))
- *(blazor)* A field's class can say warning or info, not just valid ([1296313](https://github.com/xfunc-pty-ltd/formidable/commit/1296313ff9b142f3097ba3e22c54268694d857b7))
- *(blazor)* Attach mode notices the page changing ([b62e01a](https://github.com/xfunc-pty-ltd/formidable/commit/b62e01aa4850ad2fc7d5781483383908b98e2424))
- *(blazor)* Engagement is a committed value change ([5976586](https://github.com/xfunc-pty-ltd/formidable/commit/5976586bbf3c80d586ebbf49955434cc709701a2))
- *(core)* Rule-level selection and execution on the adapter ([e9cdab5](https://github.com/xfunc-pty-ltd/formidable/commit/e9cdab5e5554178d89665653dd6c384ff9fda79b))
- *(blazor)* Derive the disclosure channels from the verdict store ([93ee620](https://github.com/xfunc-pty-ltd/formidable/commit/93ee620bcf45b40048f4f672db01827c842d7026))
- *(blazor)* The live channel defaults to the submit profile ([d0a5df9](https://github.com/xfunc-pty-ltd/formidable/commit/d0a5df9d0f140170f7ea391b3dfafa75ac1a2a1d))
- *(sample)* A success colour and one live-channel story ([2ee8e63](https://github.com/xfunc-pty-ltd/formidable/commit/2ee8e63ad8ecbd89d4ef09e33aa6173ed7462185))
- *(blazor)* Derive the required marker from the validator's rules ([9e4cda9](https://github.com/xfunc-pty-ltd/formidable/commit/9e4cda914efbcc6a48c6d9bb6b685393e7d62dd8))
- *(blazor)* Say what a form's loaded values have already earned ([da05991](https://github.com/xfunc-pty-ltd/formidable/commit/da05991f01e67e1574f1b08713bcece4b2588c0a))
- *(blazor)* Typed ChildContent and the model-level message ([da1f8f0](https://github.com/xfunc-pty-ltd/formidable/commit/da1f8f044d351b450e8a20a881211d817a751391))
- *(blazor)* A form writes the sentences the engine puts on screen ([a3445c2](https://github.com/xfunc-pty-ltd/formidable/commit/a3445c25e23430acfaacdf2449cbe5a3242f30d6))
- *(blazor)* The dialog-safe focus sequence and overflow currency ([d7d8df7](https://github.com/xfunc-pty-ltd/formidable/commit/d7d8df7b8dd3af29ece1e5f8a81a06b1a4d2561c))
- *(blazor)* Hold the valid vouch while a re-answer is on its way ([df6ab1b](https://github.com/xfunc-pty-ltd/formidable/commit/df6ab1bc3cffd9a9c88e479c9af43f7d4b7e7d17))
- *(blazor)* Resolve the engine's TimeProvider from the container ([cf4fab3](https://github.com/xfunc-pty-ltd/formidable/commit/cf4fab3070f7d6e6fd9e4e216ba53d4d2bbe47c2))
- *(sample)* The six-stage tutorial app ([491505d](https://github.com/xfunc-pty-ltd/formidable/commit/491505d5ae510e996756f2e86b8baef45dedb9f5))
- *(blazor)* The form goes inert until interactivity arrives ([d7347f5](https://github.com/xfunc-pty-ltd/formidable/commit/d7347f5aaa4f050304c531cc28ac652307ce0eaf))
- *(core)* Keep the overlap diagnostic in release builds ([d0d95cc](https://github.com/xfunc-pty-ltd/formidable/commit/d0d95cceeb16968255532cc87a3f88ec8f36713f))

### Fixed

- *(blazor)* Replace a server verdict per apply, scope the pending flag ([8a280ab](https://github.com/xfunc-pty-ltd/formidable/commit/8a280ab2d07d7a1a10867459d1f43376c97e1cb2))
- *(blazor)* Scope the post-submit re-check to the edited fields ([e674d3c](https://github.com/xfunc-pty-ltd/formidable/commit/e674d3c121044bf63e6b2d96ca4679f1e1d8a0e9))
- *(blazor)* Reconcile when the page moves the fields around ([876b597](https://github.com/xfunc-pty-ltd/formidable/commit/876b597191ab8eaa21feee57213558e8fa855eec))
- *(blazor)* Re-deliver a click the page displaced under the pointer ([d290f35](https://github.com/xfunc-pty-ltd/formidable/commit/d290f35d35efb6253f58fea0e517ebb692919e65))
- *(core)* Read scoped children as FluentValidation runs them ([b44e670](https://github.com/xfunc-pty-ltd/formidable/commit/b44e670edbaf7970d16c3d614afb357d7c470fbb))
- *(aspnetcore)* Decide strictness at build, delegate the null body ([5105111](https://github.com/xfunc-pty-ltd/formidable/commit/5105111789feddef99ed8595a45efd19eb83ae92))
- *(blazor)* Harden disposal and sanitize every diagnosed path ([8338e26](https://github.com/xfunc-pty-ltd/formidable/commit/8338e2692ad21b897e61b30e929a0beeb90edc19))
- *(aspnetcore)* Build the 400's errors without ModelState ([dd00385](https://github.com/xfunc-pty-ltd/formidable/commit/dd003859f01912fe80619a908738fa0d4fbb3eb2))
- *(sample)* Place the silent indicator, and pin what Enter does ([f164c11](https://github.com/xfunc-pty-ltd/formidable/commit/f164c11c1e0ad2e801813ae8fe4495bbb1de0f1a))
- *(sample)* Refuse a stored culture the platform does not know ([6c9c310](https://github.com/xfunc-pty-ltd/formidable/commit/6c9c31092f9401808cdb194df5c09f639b7ca9d3))
- *(core)* Make reading the wire problem trim-safe ([5ed91c2](https://github.com/xfunc-pty-ltd/formidable/commit/5ed91c29e2878901578497d2b4fb470df6999c0e))

### Performance

- Cheaper passes, bounded caches, and a trim-ready release lane ([6129599](https://github.com/xfunc-pty-ltd/formidable/commit/612959910b6f994fb5e198da1ff28a340b91124d))
- *(core)* Run a pass's stale rules in one selector-filtered call ([e2e393f](https://github.com/xfunc-pty-ltd/formidable/commit/e2e393f16a0133ae90f9bdfa1fb2cc7287daee59))
- *(blazor)* Skip the shadow map when nothing shadows ([aaad0b9](https://github.com/xfunc-pty-ltd/formidable/commit/aaad0b90d194702ff2b484ce75ea54c11b2671c5))

### Documentation

- The first documentation corpus ([12ad84e](https://github.com/xfunc-pty-ltd/formidable/commit/12ad84eadd772628b56d6bb906c8c08c34792684))
- Map behaviours to their settings in one recipes page ([48b6888](https://github.com/xfunc-pty-ltd/formidable/commit/48b688849e342c993082e750e74a5726aae147cb))
- Rewrite the learn path in the library's own voice ([54c6b9d](https://github.com/xfunc-pty-ltd/formidable/commit/54c6b9df33ba9fc678ddb21eb3d70f9e9165420d))
- Teach testing, every option, and the three-file quickstart ([1428391](https://github.com/xfunc-pty-ltd/formidable/commit/1428391dc8ba5c7a7b3c056412000919fc6fefc0))
- A truth sweep over the aria, suppression and hosting claims ([d73d64a](https://github.com/xfunc-pty-ltd/formidable/commit/d73d64aec459522270a19ee43145ee1271fe217e))
- Hold every page to the code it describes ([6f5f962](https://github.com/xfunc-pty-ltd/formidable/commit/6f5f962d7bbcd7022fdbf023d805c00e58a5cd68))
- The tutorial teaches the library stage by stage ([58f584d](https://github.com/xfunc-pty-ltd/formidable/commit/58f584d57dd85ccd8ae94e6d270db6264335d6e3))
- Recipes end at their steps and the references read scan-first ([bfef1c6](https://github.com/xfunc-pty-ltd/formidable/commit/bfef1c68364b18a5e639cc04562617df99b09385))
- Prescriptive hosting models and a plainer front door ([e89b437](https://github.com/xfunc-pty-ltd/formidable/commit/e89b4372dcb9bcdebb0296fff783f227b88ac71c))
- Answer-shaped concepts and asides in parentheses ([673b2d1](https://github.com/xfunc-pty-ltd/formidable/commit/673b2d1a776ba30879ae366b3a9423a43761bde3))
- The engine's mechanics move to one internals page ([36763c7](https://github.com/xfunc-pty-ltd/formidable/commit/36763c79faf93cad3e72bb431a141c59c077fe3e))
- Every page speaks the reader's own terms ([ef65a53](https://github.com/xfunc-pty-ltd/formidable/commit/ef65a53568251d712fc0280cd76b04a94a732ae5))
- Route a symptom from the README to its answer ([e5a4407](https://github.com/xfunc-pty-ltd/formidable/commit/e5a4407c67e2aaeefabe7ca7142dbb24df3ace1e))
- Rewrite every XML comment to the reference shape ([43fb858](https://github.com/xfunc-pty-ltd/formidable/commit/43fb8584ab5ca74129580d2260542640ffe82e17))
- True up six claims read against the code ([320f0fb](https://github.com/xfunc-pty-ltd/formidable/commit/320f0fbd259e1a8d0a4420132da42e5dd6fe0f6c))
- Hold every remark to one caveat of at most eighty words ([ffbcbcd](https://github.com/xfunc-pty-ltd/formidable/commit/ffbcbcdbe67e5a37f6d2a1bc44ab4219147cd8ba))
- The child-selection rule and how a module plan is written ([aa2fded](https://github.com/xfunc-pty-ltd/formidable/commit/aa2fded1299be41e77eadb81ec945fee86304b3e))
- Strip the publish-day markers from the packed README ([3ec565a](https://github.com/xfunc-pty-ltd/formidable/commit/3ec565a46067e3624dea19f2c1e3a979576f534b))
- Trim the packed README's hosting tail to the live docs ([f26bf59](https://github.com/xfunc-pty-ltd/formidable/commit/f26bf5924a41458863f5b767680fb0c8bdf776b2))
- Releases are the maintainer's; commits follow one shape ([e96118f](https://github.com/xfunc-pty-ltd/formidable/commit/e96118fe1813fa87a0eab3732633370065d7aca6))
- Fold the commit shape into the existing conventions section ([63d5ff6](https://github.com/xfunc-pty-ltd/formidable/commit/63d5ff62f622ea27e711f4a69902af0f1d9606b5))
- Explain the fork and pull request flow ([e3c8cb9](https://github.com/xfunc-pty-ltd/formidable/commit/e3c8cb96fde4920bc5a18796ed6973762c19d661))
- Let the packages table's dependency cells wrap ([e278362](https://github.com/xfunc-pty-ltd/formidable/commit/e2783625afaff47fba38752cf79bf1623c9e24e5))
- Break the longest dependency token across lines ([1440286](https://github.com/xfunc-pty-ltd/formidable/commit/1440286838c008d8bafb8b46e1f4e40471ca910e))
- Show a mixed verdict in the hero captures ([7b042e5](https://github.com/xfunc-pty-ltd/formidable/commit/7b042e575a5f02d1e15047106b15645d6c059ae2))
- Describe the release the way any maintainer runs it ([445c64e](https://github.com/xfunc-pty-ltd/formidable/commit/445c64ed81a94d74552b221eeb65c1248797dd1d))
- State the key's source plainly and the username's shape ([4cc0fae](https://github.com/xfunc-pty-ltd/formidable/commit/4cc0fae142bca6160239a77debd270703ca1669c))
- Trimming happens at publish, and the defaults come first-class ([bb84eb5](https://github.com/xfunc-pty-ltd/formidable/commit/bb84eb5b3c9c514fbdbdadd32dee43d27cad21b2))
- Retire the publish-day markers ([6255237](https://github.com/xfunc-pty-ltd/formidable/commit/62552376cd6712bc79a8cbe41fdae72fbcd05d11))

[2.0.0]: https://github.com/xfunc-pty-ltd/formidable/releases/tag/v2.0.0
[1.0.2]: https://github.com/xfunc-pty-ltd/formidable/releases/tag/v1.0.2
[1.0.1]: https://github.com/xfunc-pty-ltd/formidable/releases/tag/v1.0.1
[1.0.0]: https://github.com/xfunc-pty-ltd/formidable/releases/tag/v1.0.0
