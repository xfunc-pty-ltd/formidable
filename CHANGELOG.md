# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) (pre-1.0: the
`0.MINOR.PATCH` surface can still move).

## [Unreleased]

### Added

- *(core)* Profiles, the report model, and the validator seam ([d3b9e07](https://github.com/xfunc/formidable/commit/d3b9e07add81ede31ea302d5f2466d209397eeae))
- *(blazor)* The validation engine and the two form roots ([5cb6bc7](https://github.com/xfunc/formidable/commit/5cb6bc7225bc7505dad8ea91de75e4fe7e6b8637))
- *(blazor)* The headless component kit ([b4b7c86](https://github.com/xfunc/formidable/commit/b4b7c869aadfb32743aae91474e5e542aff3c0c2))
- *(aspnetcore)* Validation filters and the shared wire contract ([83f00f8](https://github.com/xfunc/formidable/commit/83f00f8b57feca993da8b1526455a10ed497d91f))
- *(sample)* The sample app, its API, and the first teaching pages ([32812e0](https://github.com/xfunc/formidable/commit/32812e09da0d3090eaed3d12b8ed9d444a17c0c4))
- *(sample)* The xfunc Soft theme and the styled validation surface ([a86d1e8](https://github.com/xfunc/formidable/commit/a86d1e8cbcf2a6f7812a0bc75fcc0344531cbd14))
- *(sample)* Teaching panels on every page ([a27d988](https://github.com/xfunc/formidable/commit/a27d9886eb042425da6b065e0400503c5d8e763d))
- *(sample)* Field state, normalize, and five more pages ([4dba94d](https://github.com/xfunc/formidable/commit/4dba94dbc26c6409a84ebd55f210d6fa3243f0cb))
- *(sample)* The event-registration workout page ([1e530e2](https://github.com/xfunc/formidable/commit/1e530e20731b3c028d1d6daffefa2012b5ad88f1))
- *(blazor)* The input base opens as the extension point ([e54aea2](https://github.com/xfunc/formidable/commit/e54aea27abfc9e58619a60f52f3ed45ea0a1fb75))
- *(blazor)* Own the server round trip and the first wiring mistakes ([5d864c6](https://github.com/xfunc/formidable/commit/5d864c616ceab47ec17ae774d1ae5655d68ea3ee))
- *(blazor)* Typed number and date inputs on the curated seam ([822bba7](https://github.com/xfunc/formidable/commit/822bba763831b5d5f271e1b2927d11757e7145bf))
- *(blazor)* LiveDebounce, validity tracking, and the diagnostics ([fd83a2f](https://github.com/xfunc/formidable/commit/fd83a2fc447f9c7f2309633f97aa3efe8774b835))
- *(blazor)* Order issues by the page, focus the first error ([fe0289f](https://github.com/xfunc/formidable/commit/fe0289f1b729de12d21b5ce0bcd3f6791cf3dbc9))
- *(core)* Memoize an async rule, run each rule once after a submit ([ff6bcb4](https://github.com/xfunc/formidable/commit/ff6bcb4d2b3ae7f09a0f0afd82f4b45a4dc006c1))
- *(blazor)* A field's class can say warning or info, not just valid ([1296313](https://github.com/xfunc/formidable/commit/1296313ff9b142f3097ba3e22c54268694d857b7))
- *(blazor)* Attach mode notices the page changing ([b62e01a](https://github.com/xfunc/formidable/commit/b62e01aa4850ad2fc7d5781483383908b98e2424))
- *(blazor)* Engagement is a committed value change ([5976586](https://github.com/xfunc/formidable/commit/5976586bbf3c80d586ebbf49955434cc709701a2))
- *(core)* Rule-level selection and execution on the adapter ([e9cdab5](https://github.com/xfunc/formidable/commit/e9cdab5e5554178d89665653dd6c384ff9fda79b))
- *(blazor)* Derive the disclosure channels from the verdict store ([93ee620](https://github.com/xfunc/formidable/commit/93ee620bcf45b40048f4f672db01827c842d7026))
- *(blazor)* The live channel defaults to the submit profile ([d0a5df9](https://github.com/xfunc/formidable/commit/d0a5df9d0f140170f7ea391b3dfafa75ac1a2a1d))
- *(sample)* A success colour and one live-channel story ([2ee8e63](https://github.com/xfunc/formidable/commit/2ee8e63ad8ecbd89d4ef09e33aa6173ed7462185))
- *(blazor)* Derive the required marker from the validator's rules ([9e4cda9](https://github.com/xfunc/formidable/commit/9e4cda914efbcc6a48c6d9bb6b685393e7d62dd8))
- *(blazor)* Say what a form's loaded values have already earned ([da05991](https://github.com/xfunc/formidable/commit/da05991f01e67e1574f1b08713bcece4b2588c0a))
- *(blazor)* Typed ChildContent and the model-level message ([da1f8f0](https://github.com/xfunc/formidable/commit/da1f8f044d351b450e8a20a881211d817a751391))
- *(blazor)* A form writes the sentences the engine puts on screen ([a3445c2](https://github.com/xfunc/formidable/commit/a3445c25e23430acfaacdf2449cbe5a3242f30d6))
- *(blazor)* The dialog-safe focus sequence and overflow currency ([d7d8df7](https://github.com/xfunc/formidable/commit/d7d8df7b8dd3af29ece1e5f8a81a06b1a4d2561c))
- *(blazor)* Hold the valid vouch while a re-answer is on its way ([df6ab1b](https://github.com/xfunc/formidable/commit/df6ab1bc3cffd9a9c88e479c9af43f7d4b7e7d17))
- *(blazor)* Resolve the engine's TimeProvider from the container ([cf4fab3](https://github.com/xfunc/formidable/commit/cf4fab3070f7d6e6fd9e4e216ba53d4d2bbe47c2))
- *(sample)* The six-stage tutorial app ([491505d](https://github.com/xfunc/formidable/commit/491505d5ae510e996756f2e86b8baef45dedb9f5))
- *(blazor)* The form goes inert until interactivity arrives ([d7347f5](https://github.com/xfunc/formidable/commit/d7347f5aaa4f050304c531cc28ac652307ce0eaf))

### Fixed

- *(blazor)* Replace a server verdict per apply, scope the pending flag ([8a280ab](https://github.com/xfunc/formidable/commit/8a280ab2d07d7a1a10867459d1f43376c97e1cb2))
- *(blazor)* Scope the post-submit re-check to the edited fields ([e674d3c](https://github.com/xfunc/formidable/commit/e674d3c121044bf63e6b2d96ca4679f1e1d8a0e9))
- *(blazor)* Reconcile when the page moves the fields around ([876b597](https://github.com/xfunc/formidable/commit/876b597191ab8eaa21feee57213558e8fa855eec))
- *(blazor)* Re-deliver a click the page displaced under the pointer ([d290f35](https://github.com/xfunc/formidable/commit/d290f35d35efb6253f58fea0e517ebb692919e65))
- *(core)* Read scoped children as FluentValidation runs them ([b44e670](https://github.com/xfunc/formidable/commit/b44e670edbaf7970d16c3d614afb357d7c470fbb))
- *(aspnetcore)* Decide strictness at build, delegate the null body ([5105111](https://github.com/xfunc/formidable/commit/5105111789feddef99ed8595a45efd19eb83ae92))
- *(blazor)* Harden disposal and sanitize every diagnosed path ([8338e26](https://github.com/xfunc/formidable/commit/8338e2692ad21b897e61b30e929a0beeb90edc19))
- *(aspnetcore)* Build the 400's errors without ModelState ([dd00385](https://github.com/xfunc/formidable/commit/dd003859f01912fe80619a908738fa0d4fbb3eb2))
- *(sample)* Place the silent indicator, and pin what Enter does ([f164c11](https://github.com/xfunc/formidable/commit/f164c11c1e0ad2e801813ae8fe4495bbb1de0f1a))

### Performance

- Cheaper passes, bounded caches, and a trim-ready release lane ([6129599](https://github.com/xfunc/formidable/commit/612959910b6f994fb5e198da1ff28a340b91124d))
- *(core)* Run a pass's stale rules in one selector-filtered call ([e2e393f](https://github.com/xfunc/formidable/commit/e2e393f16a0133ae90f9bdfa1fb2cc7287daee59))
- *(blazor)* Skip the shadow map when nothing shadows ([aaad0b9](https://github.com/xfunc/formidable/commit/aaad0b90d194702ff2b484ce75ea54c11b2671c5))

### Documentation

- The first documentation corpus ([12ad84e](https://github.com/xfunc/formidable/commit/12ad84eadd772628b56d6bb906c8c08c34792684))
- Map behaviours to their settings in one recipes page ([48b6888](https://github.com/xfunc/formidable/commit/48b688849e342c993082e750e74a5726aae147cb))
- Rewrite the learn path in the library's own voice ([54c6b9d](https://github.com/xfunc/formidable/commit/54c6b9df33ba9fc678ddb21eb3d70f9e9165420d))
- Teach testing, every option, and the three-file quickstart ([1428391](https://github.com/xfunc/formidable/commit/1428391dc8ba5c7a7b3c056412000919fc6fefc0))
- A truth sweep over the aria, suppression and hosting claims ([d73d64a](https://github.com/xfunc/formidable/commit/d73d64aec459522270a19ee43145ee1271fe217e))
- Hold every page to the code it describes ([6f5f962](https://github.com/xfunc/formidable/commit/6f5f962d7bbcd7022fdbf023d805c00e58a5cd68))
- The tutorial teaches the library stage by stage ([58f584d](https://github.com/xfunc/formidable/commit/58f584d57dd85ccd8ae94e6d270db6264335d6e3))
- Recipes end at their steps and the references read scan-first ([bfef1c6](https://github.com/xfunc/formidable/commit/bfef1c68364b18a5e639cc04562617df99b09385))
- Prescriptive hosting models and a plainer front door ([e89b437](https://github.com/xfunc/formidable/commit/e89b4372dcb9bcdebb0296fff783f227b88ac71c))
- Answer-shaped concepts and asides in parentheses ([673b2d1](https://github.com/xfunc/formidable/commit/673b2d1a776ba30879ae366b3a9423a43761bde3))
- The engine's mechanics move to one internals page ([36763c7](https://github.com/xfunc/formidable/commit/36763c79faf93cad3e72bb431a141c59c077fe3e))
- Every page speaks the reader's own terms ([ef65a53](https://github.com/xfunc/formidable/commit/ef65a53568251d712fc0280cd76b04a94a732ae5))
- Route a symptom from the README to its answer ([e5a4407](https://github.com/xfunc/formidable/commit/e5a4407c67e2aaeefabe7ca7142dbb24df3ace1e))
- Rewrite every XML comment to the reference shape ([43fb858](https://github.com/xfunc/formidable/commit/43fb8584ab5ca74129580d2260542640ffe82e17))
- True up six claims read against the code ([320f0fb](https://github.com/xfunc/formidable/commit/320f0fbd259e1a8d0a4420132da42e5dd6fe0f6c))
- Hold every remark to one caveat of at most eighty words ([ffbcbcd](https://github.com/xfunc/formidable/commit/ffbcbcdbe67e5a37f6d2a1bc44ab4219147cd8ba))
- The child-selection rule and how a module plan is written ([aa2fded](https://github.com/xfunc/formidable/commit/aa2fded1299be41e77eadb81ec945fee86304b3e))
- Strip the publish-day markers from the packed README ([3ec565a](https://github.com/xfunc/formidable/commit/3ec565a46067e3624dea19f2c1e3a979576f534b))
- Trim the packed README's hosting tail to the live docs ([f26bf59](https://github.com/xfunc/formidable/commit/f26bf5924a41458863f5b767680fb0c8bdf776b2))
- Releases are the maintainer's; commits follow one shape ([e96118f](https://github.com/xfunc/formidable/commit/e96118fe1813fa87a0eab3732633370065d7aca6))
- Fold the commit shape into the existing conventions section ([63d5ff6](https://github.com/xfunc/formidable/commit/63d5ff62f622ea27e711f4a69902af0f1d9606b5))

<!-- publish-day: verify -->
[Unreleased]: https://github.com/xfunc/formidable/commits/main
