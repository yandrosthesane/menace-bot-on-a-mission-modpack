// ==UserScript==
// @name         SaneSRSEngine
// @namespace    sane-japanese
// @version      2025-12-30
// @match        https://kitsun.io/deck/*/reviews
// @match        https://kitsun.io/deck/*/lessons
// @author       YandrosTheSane
// @license      MIT; http://opensource.org/licenses/MIT
// @description  (Service Agnostic ?) SRS Predicate Engine
// @grant        GM_addStyle
// @grant        GM_getValue
// @grant        GM_setValue
// @connect      api.kitsun.io
// ==/UserScript==

(function () {
  "use strict";

  //region Logging
  const LOG_SCRIPT_PREFIX = "[SSRSE~";
  const LOG_CHANNELS = {
    // General system status
    dev: false,   // temporary, exploratory logs
    debug: false,  // permanent low-level diagnostics info
    sane: true,   // permanent high-level diagnostics semantic info

    // Service actions logic chain
    actionDebug: true, // permanent low-level info on action chain behavior
    actionSane: true, // permanent high-level info on action chain behavior
  };
  const makeLogger = (enabled, channel) => enabled ? (...args) => console.log(`${LOG_SCRIPT_PREFIX}${channel}`, ...args) : () => {};
  const devLog   = makeLogger(LOG_CHANNELS.dev,   "DEV]");
  const debugLog = makeLogger(LOG_CHANNELS.debug, "DEBUG]");
  const saneLog  = makeLogger(LOG_CHANNELS.sane,  "SANE]");
  const actionDebugLog   = makeLogger(LOG_CHANNELS.actionDebug, "ACTION:DEBUG");
  const actionSaneLog    = makeLogger(LOG_CHANNELS.actionSane,  "ACTION:SANE");
  //endregion



  //region USER EDITABLE CONFIGURATION
  const BEHAVIOR_GRAPH = {
    nodes: {
      ReviewsLoaded: {
        entrypoint: true,
        run: ({ actions, context, args }) => ({ next: args.next, context: actions.indexReviews({reviews:context.normalizedValue.reviews, context})  }),
        args: { next: "RefreshCurrentReview" }
      },
      ReviewResolved: {
        entrypoint: true,
        run: ({ actions, context, args }) => ({ next: args.next, context: actions.clearCurrentCardUserState({ context }) }),
        args: { next: "RefreshCurrentReview" }
      },
      RefreshCurrentReview: {
        run: ({ actions, context, args }) => ({ next: args.next, context: actions.setContextCurrentCard({ context }) }),
        retry: { attempts: 5, delay: 16, predicate: ({ context }) => context.currentCard == null },
        args: { next: "UpdateUI" }
      },
      UpdateInputUI: {
        run: ({ actions, context }) => {
          actions.renderInput({actions,context});
          return { next: null, context };
        },
        entrypoint: true,
        end: true
      },
      AnswerInOnyomi: {
        entrypoint: true,
        run: ({ actions, context, args }) => ({ next: args.next, context: {...context, normalizedValue: actions.toOnyomi({value: context.normalizedValue}) }}),
        args: { next: "InterceptServiceAnswer" }
      },
      AnswerInKunyomi: {
        entrypoint: true,
        run: ({ actions, context, args }) => ({ next: args.next, context: {...context, normalizedValue: actions.toKunyomi({value: context.normalizedValue}) }}),
        args: { next: "InterceptServiceAnswer" }
      },
      LockAndSubmit: {
        entrypoint: true,
        run: ({ actions, context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              submissionLocked: true
            }
          }}),
        args: { next: "InterceptServiceAnswer" }
      },
      InterceptServiceAnswer: {
        entrypoint: true,
        run: ({actions, context, args}) => {
          const { originalEvent } = context;
          originalEvent.preventDefault();
          originalEvent.stopPropagation();
          originalEvent.stopImmediatePropagation();

          return { next: args.ignoreEventTypes.includes(originalEvent.type) ? null : args.next, context: context }
        },
        args: { next: "EvaluateAnswerOrSubmit", "ignoreEventTypes": ["keydown"] },
      },
      EvaluateAnswerOrSubmit: {
        run: ({ actions, context, args }) => {
          for (const { predicate, next } of args.paths) {
            if (predicate({ actions, context })) {
              return { next, context };
            }
          }
          return { next: args.default, context };
        },
        args: {
          paths: [
            { predicate: ({ actions, context }) => context.currentCardUserState.submissionLocked === true, next: "Submit" },
          ],
          default: "SetContextCurrentCard"
        }
      },
      SetContextCurrentCard: {
        run: ({actions, context, args}) => ({ next: args.next, context: actions.setContextCurrentCard({ context })  }),
        args: { next: "MatchInputBranch" },
      },
      MatchInputBranch: {
        run: ({ actions, context, args }) => {
          for (const { predicate, next } of args.paths) {
            if (predicate({ actions, context })) {
              return { next, context };
            }
          }
          return { next: args.default, context };
        },
        args: {
          paths: [
            { predicate: ({ actions, context }) => actions.inputMatchMeaning({ value: context.normalizedValue, candidates: context.currentCard.meanings }), next: "OnMeaningMatched" },
            { predicate: ({ actions, context }) => actions.inputMatchReading({ value: context.normalizedValue, candidates: context.currentCard.onyomi }), next: "OnOnyomiMatched" },
            { predicate: ({ actions, context }) => actions.inputMatchReading({ value: context.normalizedValue, candidates: context.currentCard.kunyomi }), next: "OnKunyomiMatched" }
          ],
          default: "OnEvaluationFailure"
        }
      },
      OnMeaningMatched: {
        run: ({ context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              meaningConfirmed: true
            }
          }
        }),
        args: { next: "CheckRules" }
      },
      OnOnyomiMatched: {
        run: ({ context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              readingsUsed: new Set([ ...context.currentCardUserState.readingsUsed, context.normalizedValue]),
              onyomiUsed: new Set([ ...context.currentCardUserState.onyomiUsed, context.normalizedValue ])
            }
          }
        }),
        args: { next: "UpsertServiceValidAnswer" }
      },
      /*OnKunyomiMatched: {
        run: ({ context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              readingsUsed: new Set([...context.currentCardUserState.readingsUsed, context.normalizedValue]),
              kunyomiUsed: new Set([...context.currentCardUserState.kunyomiUsed, context.normalizedValue])
            }
          }
        }),
        args: { next: "UpsertServiceValidAnswer" }
      },*/
      OnKunyomiMatched: {
        run: ({ context, args }) => {
          const kunyomiStem = (r) => r.split(normalizedDotCharacter)[0];
          const normalizedDot = (s) => s.replace(/[.\uFF0E\u30FB\u00B7\u2219]/g, normalizedDotCharacter ?? "・");
          const stem = kunyomiStem(context.normalizedValue);
          const sharedStemReadings = context.currentCard.kunyomi.map(normalizedDot).filter(r =>  kunyomiStem(r) === stem);
          return {
            next: args.next,
            context: {
              ...context,
              currentCardUserState: {
                ...context.currentCardUserState,
                readingsUsed: new Set([
                  ...context.currentCardUserState.readingsUsed,
                  ...sharedStemReadings
                ]),
                kunyomiUsed: new Set([
                  ...context.currentCardUserState.kunyomiUsed,
                  ...sharedStemReadings
                ])
              }
            }
          };
        },
        args: { next: "UpsertServiceValidAnswer" }
      },
      UpsertServiceValidAnswer: {
        run: ({ actions, context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              answer: actions.inputMatchReading({ value: context.normalizedValue, candidates: context.currentCard.domainValidReading }) ?? context.currentCardUserState.answer
            }
          }
        }),
        args: { next: "CheckRules" }
      },
      OnEvaluationFailure: {
        run: ({ context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              submissionLocked: true,
              answer: context.currentCardUserState.cardLevelCleared ? context.currentCardUserState.answer : context.normalizedValue
            }
          }
        }),
        args: { next: "ClearAnswer" }
      },
      CheckRules: {
        run: ({ actions, context, args }) => {
          return {
            next: actions.rules.map(rule => rule({context})).filter(r => r.ruleApplies !== false && r.rulePass === false).length === 0 ? args.onSuccess : args.onPending,
            context
          };
        },
        args: { onSuccess: "SetLevelCleared", onPending: "ClearAnswer" }
      },
      SetLevelCleared: {
        run: ({ context, args }) => ({
          next: args.next,
          context: {
            ...context,
            currentCardUserState: {
              ...context.currentCardUserState,
              cardLevelCleared: true
            }
          }
        }),
        args: { next: "ClearAnswer" }
      },
      ClearAnswer: {
        run: ({ actions, context, args }) => {
          actions.clearAnswer()
          context.clearPreview = true;
          return { next: args.next, context };
        },
        args: { next: "UpdateUI" }
      },
      UpdateUI: {
        run: ({ actions, context }) => {
          actions.renderMeaningConfirmation({actions,context});
          actions.renderInput({actions,context});
          actions.renderReadingPanel({actions,context});
          actions.renderRequirements({actions, context });
          return { next: null, context };
        },
        end: true
      },
      Submit: {
        run: ({ actions, context }) => {
          actions.submitAnswer({ answer: context.currentCardUserState.answer });
          return { next: null, context };
        },
        end: true
      },
    }
  }

  //Stupid behaviour I have to override because of lessons flow
  const normalizedDotCharacter = "・";
  const customControlCharsToStrip = {type:"replace",args:[/[&$é*]/g,""]}

  const CLEAN_CARD_USER_STATE =  {
    kanji: null,
    meaningConfirmed: false,
    readingsUsed: [],
    onyomiUsed: [],
    kunyomiUsed: [],
    answer: null,
    cardLevelCleared: false,
    submissionLocked: false
  }
  let seriazableState = { currentCardUserState: {...CLEAN_CARD_USER_STATE} }

  const CONFIGURATION = {
    service: "Kitsun",
    modules: {
      RulesModule: {
        requireMeaning: {
          factory: "meaningRequiredBySrsMap_",
          config: {
            map: {
              default: 1
            },
            text: {
              required: "Meaning required"
            }
          }
        },
        requiredReadings: {
          factory: "requiredReadingsBySrsMap_",
          config: {
            map: {
              1: 1,
              2: 1,
              3: 1,
              4: 2,
              5: 4,
              default: 9
            },
            text: {
              required: "{n} reading{s} required"
            }
          }
        }
      },
      UserInputsModule: {
        inputMatchMeaning: { factory: "matchFuzzy_", config:{
            maxDistance: 2,
            enableTokenMatch: true,
            normalizeInput: [
              { type: "lowercase" },
              { type: "replace", args: [/[^a-z0-9\s]/gi, ""] },
              { type: "replace", args: [/\s+/g, " "] },
            ],
            normalizeCandidate: [
              { type: "lowercase" },
              { type: "replace", args: [/[^a-z0-9\s]/gi, ""] },
              { type: "replace", args: [/\s+/g, " "] },
            ],
          } } ,
        inputMatchReading: { factory: "matchExact_", config: {
            /* exact match, no fuzziness */
            enableTokenMatch: false,
            normalizeInput: [
              { type: "normalizeDot" },
            ],
            normalizeCandidate: [
              { type: "normalizeDot" },
            ],
          }
        },
        toKunyomi: {
          factory: "normalizeString_",
          config: [
            customControlCharsToStrip,
            { type: "lowercase" },
            { type: "doubleConsonantToSmallTsu" },
            { type: "normalizeN" },
            { type: "romajiToHiragana" },
            { type: "normalizeDot" },
            { type: "replace", args: [/\s+/g, ""] },
            { type: "filterInvalidKunyomi" }
          ]
        },
        toOnyomi: {
          factory: "normalizeString_",
          config: [
            customControlCharsToStrip,
            { type: "lowercase" },
            { type: "romajiToHiragana" },
            { type: "hiraganaToKatakana" },
            { type: "normalizeDot" },
            { type: "replace", args: [/\s+/g, ""] },
            { type: "filterInvalidOnyomi" }
          ]
        },
      },
      StateModule: {
        getState: {
          factory: "getState_",
          config: {
            serializableState: seriazableState,
            predicate: ({ serializableState }) => ({ ...seriazableState }),
            predicateArgs: {}
          }
        },
        clearCurrentCardUserState: {
          factory: "clearCurrentCardUserState_",
          config: {
            cleanCardUserState: CLEAN_CARD_USER_STATE
          }
        },
        commitState: {
          factory: "commitState_",
          config: {
            serializableState: seriazableState,
            predicate: ({ state }) => { seriazableState = state },
            predicateArgs: {}
          }
        }
      },
      ServiceBoundariesModule: {
        describeBoundaryConfig: { factory: "describeBoundaryConfig_" },
        onArrowClick: { factory: "onElementClick_", config: { predicate: (e, predicateArgs, element) => element && e.type === "click", predicateArgs: { selector: "#nextans" } } },
        onSubmitRawAnswer: { factory: "onKey_", config: { predicate: (e, predicateArgs) => !e.repeat && e.shiftKey === false && e.key === "Enter" && e.target?.id === "typeans", predicateArgs: {} } },
        onInputChange: { factory: "onKey_", config: { predicate: (e, predicateArgs) => e.target?.id === "typeans", predicateArgs: {} } },
        onSubmitOnyomiShortcut: { factory: "onKey_", config: { predicate: (e, predicateArgs) => (e.key === "o" && e.altKey) || e.key==="&" || e.key==="$", predicateArgs: {} } },
        onSubmitKunyomiShortcut: { factory: "onKey_", config: { predicate: (e, predicateArgs) => (e.key === "k" && e.altKey) || e.key==="é"  || e.key==="*", predicateArgs: {} } },
        onLockAndSubmitShortcut: { factory: "onKey_", config: { predicate: (e, predicateArgs) => !e.repeat && e.key === "Enter" && e.shiftKey === true, predicateArgs: {} } },
        onReviewsLoaded: { factory: "onEvent_", config: { predicate: (e, predicateArgs) => e.type === "sane-srs:reviews-loaded", predicateArgs: {} } },
        onReviewResolved: { factory: "onEvent_", config: { predicate: (e, predicateArgs) => e.type === "sane-srs:review-resolved", predicateArgs: {} } },
      },
      ServiceCapabilitiesModule: {
        handleXHR: { factory: "handleXHR_", config: { routes: [
              { predicate: (xhr, { path }) => xhr.responseURL.includes(path) && !xhr.responseURL.includes("/correct") && !xhr.responseURL.includes("/wrong"),
                predicateArgs: { path: "/general/reviews" }, handler: "emitReviewsLoaded" },

              { predicate: (xhr, { path }) => xhr.responseURL.includes(path),
                predicateArgs: { path: "/general/reviews/correct" }, handler: "emitReviewResolved", handlerArgs: { outcome: "correct" } },

              { predicate: (xhr, { path }) => xhr.responseURL.includes(path),
                predicateArgs: { path: "/general/reviews/wrong" }, handler: "emitReviewResolved", handlerArgs: { outcome: "wrong" } }

            ] } },
        emitReviewsLoaded: { factory: "emitReviewsLoaded_", config: {
            listKey: `${location.pathname.includes("/reviews") ? "reviews" : "lessons"}`,
            detailKey: "reviews",
            eventName: "sane-srs:reviews-loaded"
          } },
        emitReviewResolved: { factory: "emitReviewResolved_", config: {
            eventName: "sane-srs:review-resolved"
          } },
        setContextCurrentCard: {
          factory: "setContextCurrentCard_",
          config: {
            keySelector: ".reviews.front .main_wrap > p",
            mapper: (review) => {
              const split = (v) => (v || "").split(/[,\u3001\uFF0C]/).map(s => s.trim()).filter(Boolean)

              const intersectCount = (a, b) => {
                const setB = new Set(b);
                return a.filter(r => setB.has(r)).length;
              };

              const fields = review.card.cardFields;
              const get = name => split(fields.find(f => f.name === name)?.value);

              const onyomi = get("Onyomi");
              const kunyomi = get("Kunyomi");

              // domainValidReading are reading that make sense to learn according to the kanji readings frequency distribution.
              const domainValidReading = get("All Readings");


              return {
                kanji: fields.find(f => f.name === "Kanji")?.value,
                srs: review.SRSLevel,
                domainValidReading: domainValidReading,
                meanings: get("Meanings"),
                onyomi,
                kunyomi,
                onyomiDomainCount: intersectCount(domainValidReading, onyomi),
                kunyomiDomainCount: intersectCount(domainValidReading, kunyomi),
              };
            },
            mapperArgs: {}
          }
        },
        indexReviews: { factory: "indexReviews_", config: {
            extractIndex: (review, { field }) => review.card.cardFields.find(f => f.name === field)?.value,
            extractIndexArgs: { field: "Kanji" }
          }},
        submitAnswer: {
          factory: "submitAnswer_",
          config: {
            getButton: () => document.getElementById("nextans"),
            getInput: () => document.getElementById("typeans")
          }
        },
        clearAnswer: {
          factory: "clearAnswer_",
          config: {
            getInput: () => document.getElementById("typeans"),
          }
        },
      },
      InterceptionsModule: {
        handleAnswer: {
          meta: "Execute the rule engine on the submitted answer, trigger side effects as needed",
          factory: "bind_",
          config: {
            boundaries: {keydown: ["onSubmitRawAnswer"],keyup: ["onSubmitRawAnswer"]},
            action: "runGraph",
            actionArgs: { entrypoint: "InterceptServiceAnswer" },
            fire: "once",
          }
        },
        handleInputChange: {
          meta: "Update the InputUI",
          factory: "bind_",
          config: {
            boundaries: {keyup: ["onInputChange"]},
            action: "runGraph",
            actionArgs: { entrypoint: "UpdateInputUI" },
            fire: "once",
          }
        },
        handleKunyomiAnswer: {
          meta: "Execute the rule engine on the submitted answer, trigger side effects as needed",
          factory: "bind_",
          config: {
            boundaries: {keyup: ["onSubmitKunyomiShortcut"]},
            action: "runGraph",
            actionArgs: { entrypoint: "AnswerInKunyomi" },
            fire: "once",
          }
        },
        handleOnyomiAnswer: {
          meta: "Execute the rule engine on the submitted answer, trigger side effects as needed",
          factory: "bind_",
          config: {
            boundaries: {keyup: ["onSubmitOnyomiShortcut"]},
            action: "runGraph",
            actionArgs: { entrypoint: "AnswerInOnyomi" },
            fire: "once",
          }
        },
        handleLockAndSubmit: {
          meta: "Block further submissions and submit the answer",
          factory: "bind_",
          config: {
            boundaries: {keyup: ["onLockAndSubmitShortcut"]},
            action: "runGraph",
            actionArgs: { entrypoint: "LockAndSubmit" },
            fire: "once",
          }
        },
        onReviewsLoaded: {
          meta: "Initialize session when review batch is loaded",
          factory: "bind_",
          config: {
            boundaries: { "sane-srs:reviews-loaded": ["onReviewsLoaded"] },
            action: "runGraph",
            actionArgs: { entrypoint: "ReviewsLoaded" },
            fire: "once",
          }
        },
        onReviewResolved: {
          meta: "Reset session after correct / wrong / ignore",
          factory: "bind_",
          config: {
            boundaries: { "sane-srs:review-resolved": ["onReviewResolved"] },
            action: "runGraph",
            actionArgs: { entrypoint: "ReviewResolved" },
            fire: "once",
          }
        }
      },
      ServiceActionsModule: {
        runGraph: { factory: "runGraph_", config: { graph: BEHAVIOR_GRAPH }},
      },
      ServiceRendersModule: {
        playAnimation: { factory: "playAnimation_", config: {} },
        renderRequirements: { factory: "renderRequirements_", config: {
            style: {
              animation: {
                update: {
                  keyframes: [{ opacity: 0.6 }, { opacity: 1 }],
                  options: { duration: 120, easing: "ease-out" }
                }
              },
            }
          } },
        renderMeaningConfirmation: { factory: "renderMeaningConfirmation_", config: {
            style: {
              animation: {
                show: {
                  keyframes: [{ opacity: 0 }, { opacity: 1 }],
                  options: { duration: 300, easing: "ease-out", fill: "forwards" }
                }
              },
              minHeight: "1.6em"
            }
          } },
        renderReadingPanel:{ factory: "renderReadingPanel_", config: {
            renderReading: (kanji, reading) => {
              if (!reading.includes(normalizedDotCharacter)) {
                return `
                <ruby class="tm-reading-container">
                  <rt class="tm-furigana">${reading}</rt>
                  <span class="tm-kanji">${kanji}</span>
                </ruby>
              `;
              }
              const [kanjiReading, okurigana] = reading.split(normalizedDotCharacter);
              /*<span class="tm-okurigana"><rt>${kanjiReading}</rt>${okurigana}</span>*/
              return `
                <ruby class="tm-reading-container">
                  <table>
                    <tr>
                      <td class="tm-furigana">${kanjiReading}</td>
                      <td></td>
                    </tr>
                    <tr>
                      <td>${kanji}</td>
                      <td>${okurigana}</td>
                    </tr>
                  </table>
                </ruby>
              `;
            },
            style: {
              animation: {
                on: {
                  keyframes: [
                    { opacity: 0, transform: "translateY(-4px)" },
                    { opacity: 1, transform: "translateY(0)" }
                  ],
                  options: { duration: 160, easing: "ease-out" }
                },
                kun: {
                  keyframes: [
                    { opacity: 0, transform: "translateY(-4px)" },
                    { opacity: 1, transform: "translateY(0)" }
                  ],
                  options: { duration: 160, easing: "ease-out" }
                }
              }
            }
          } },
        renderInput:{ factory: "renderInput_", config: {
            renderOnPreview: (onPreviewEl, onyomi) => {

              saneLog("renderOnPreview", onPreviewEl)

              const label = "音: ";
              onPreviewEl.innerHTML = onyomi ? `<div className="tm-preview tm-preview-valid">
                <span className="tm-preview-label">${label}</span>
                <span className="tm-preview-value">${onyomi}</span>
              </div>` : ``;



              saneLog("renderOnPreview after", onPreviewEl)

            },
            renderKunPreview: (kunPreviewEl, kunyomi) => {

              saneLog("renderKunPreview", kunPreviewEl, kunyomi)

              const label = "訓: ";
              kunPreviewEl.innerHTML =kunyomi ? `
                <div class="tm-preview tm-preview-valid">
                  <span class="tm-preview-label">${label}</span>
                  <span class="tm-preview-value">${kunyomi}</span>
                </div>
              ` : ``;
            },
            style: {
              animation: {
                update: {
                  keyframes: [{ opacity: 0.7 }, { opacity: 1 }],
                  options: { duration: 100, easing: "ease-out" }
                }
              },
            }
          } },
      },
    },
    modulesResolution: ["RulesModule", "UserInputsModule", "StateModule", "ServiceCapabilitiesModule", "ServiceBoundariesModule", "ServiceRendersModule", "ServiceActionsModule", "InterceptionsModule"]
  };
  //endregion

  // If you ever feel the need to edit something under here you should do it and send me a feature request.
  console.groupCollapsed("SaneSRSEngine Loading, logging", LOG_CHANNELS);

  //region Module Binding
  const buildRegistry = (registry, config, dependencies) => {
    saneLog("Building registry", Object.keys(config));

    const resolved = {};
    Object.entries(config).forEach(([instanceName, spec]) => {
      debugLog("Resolving instance", instanceName, "using factory", spec.factory);

      const factory = registry[spec.factory];
      if (typeof factory !== "function") {
        throw new Error(`Unknown factory "${spec.factory}" for instance "${instanceName}"`);
      }

      resolved[instanceName] = factory({
        dependencies,
        config: spec.config,
      });
    });

    saneLog("Registry built");
    return resolved;
  };

  const bindModules_ = (modules) => (CONFIGURATION) => {
    console.groupCollapsed("Binding modules", CONFIGURATION.modulesResolution);

    const dependencies = {};

    CONFIGURATION.modulesResolution.forEach((moduleName) => {
      console.groupCollapsed("Binding module", moduleName);

      const moduleEntry = modules[moduleName];
      const moduleConfig = CONFIGURATION.modules[moduleName];

      if (!moduleEntry) {
        throw new Error(`Missing module "${moduleName}"`);
      }

      dependencies[moduleName] =
        buildRegistry(moduleEntry.registry, moduleConfig, dependencies);

      console.groupEnd();
      saneLog("Module binded", moduleName);
    });
    console.groupEnd();
    saneLog("All modules bound");
    return dependencies;
  };

  //endregion

  //region Rules Module
  const RulesModule = (() => {
    saneLog("Init RulesModule");

    const registry = {
      meaningRequiredBySrsMap_: ({ dependencies, config }) => {
        debugLog("Registering rule: meaningRequiredBySrsMap", config);
        const { map, text } = config;

        return ({ context }) => ({
          ruleApplies: (map[context.currentCard.srs] ?? map.default ?? 0) === 1,
          rulePass: context.currentCardUserState.meaningConfirmed === true,
          message: text.required
        })
      },
      requiredReadingsBySrsMap_: ({ dependencies, config }) => {
        debugLog("Registering rule: requiredReadingsBySrsMap", config);
        const { map,text } = config;

        return ({ context }) => ({
          ruleApplies: (map[context.currentCard.srs] ?? map.default ?? 0) !== 0,
          rulePass: context.currentCardUserState.readingsUsed.size >= Math.min((map[context.currentCard.srs] ?? map.default ?? 0), new Set([...(context.currentCard.domainValidReading || [])]).size),
          message: text.required
            .replace("{n}", String(Math.min((map[context.currentCard.srs] ?? map.default ?? 0), new Set([...(context.currentCard.domainValidReading || [])]).size)))
            .replace("{s}", String(Math.min((map[context.currentCard.srs] ?? map.default ?? 0), new Set([...(context.currentCard.domainValidReading || [])]).size)) > 1 ? "s" : "")
        })
      }
    };

    saneLog("RulesModule registry", Object.keys(registry));
    return { RulesModule: { registry } };
  })();
  //endregion

  //region User Inputs Module
  const UserInputsModule = (() => {
    saneLog("Init UserInputsModule");

    const HIRAGANA_TABLE = {
      // --- explicit small kana (half-width standard) ---
      xtsu: "っ", ltsu: "っ",

      xya: "ゃ", lya: "ゃ",
      xyu: "ゅ", lyu: "ゅ",
      xyo: "ょ", lyo: "ょ",

      xa: "ぁ", la: "ぁ",
      xi: "ぃ", li: "ぃ",
      xu: "ぅ", lu: "ぅ",
      xe: "ぇ", le: "ぇ",
      xo: "ぉ", lo: "ぉ",

      // --- y-combinations ---
      kya: "きゃ", kyu: "きゅ", kyo: "きょ",
      sha: "しゃ", shu: "しゅ", sho: "しょ",
      cha: "ちゃ", chu: "ちゅ", cho: "ちょ",
      nya: "にゃ", nyu: "にゅ", nyo: "にょ",
      hya: "ひゃ", hyu: "ひゅ", hyo: "ひょ",
      mya: "みゃ", myu: "みゅ", myo: "みょ",
      rya: "りゃ", ryu: "りゅ", ryo: "りょ",
      gya: "ぎゃ", gyu: "ぎゅ", gyo: "ぎょ",
      bya: "びゃ", byu: "びゅ", byo: "びょ",
      pya: "ぴゃ", pyu: "ぴゅ", pyo: "ぴょ",
      ja: "じゃ", ju: "じゅ", jo: "じょ",

      // --- voiced (dakuten) ---
      ga: "が", gi: "ぎ", gu: "ぐ", ge: "げ", go: "ご",
      za: "ざ", ji: "じ", zu: "ず", ze: "ぜ", zo: "ぞ",
      da: "だ", di: "ぢ",  dzu: "づ", du: "づ", de: "で", do: "ど",
      ba: "ば", bi: "び", bu: "ぶ", be: "べ", bo: "ぼ",

      // --- semi-voiced (handakuten) ---
      pa: "ぱ", pi: "ぴ", pu: "ぷ", pe: "ぺ", po: "ぽ",

      // --- base syllables ---
      ka: "か", ki: "き", ku: "く", ke: "け", ko: "こ",
      sa: "さ", shi: "し", su: "す", se: "せ", so: "そ",
      ta: "た", chi: "ち", tsu: "つ", te: "て", to: "と",
      na: "な", ni: "に", nu: "ぬ", ne: "ね", no: "の",
      ha: "は", hi: "ひ", fu: "ふ", he: "へ", ho: "ほ",
      ma: "ま", mi: "み", mu: "む", me: "め", mo: "も",
      ya: "や", yu: "ゆ", yo: "よ",
      ra: "ら", ri: "り", ru: "る", re: "れ", ro: "ろ",
      wa: "わ", wo: "を", n: "ん",

      // --- vowels ---
      a: "あ", i: "い", u: "う", e: "え", o: "お"
    };
    debugLog("HIRAGANA_TABLE constant", HIRAGANA_TABLE)

    // DO NOT PUT THIS IN THE REGISTRY DIRECTLY
    const normalizeString_ = ({ config }) => {
      const steps = config;

      debugLog("Registering user input normalizeString_: meaningRequiredBySrsMap", config);

      const pipeline = steps.map(step => {
        if (!step || typeof step.type !== "string") {
          debugLog("normalizeString_: invalid step", step);
          throw new Error("normalizeString_: each step must have a type");
        }

        const args = Array.isArray(step.args) ? step.args : [];

        switch (step.type) {
          case "identity":
            return s => s;

          case "lowercase":
            return s => s.toLowerCase();

          case "replace":
            (args.length === 2 && args[0] instanceof RegExp) ||
            (() => {
              debugLog("normalizeString_: invalid replace args", args);
              throw new Error("replace step requires args: [RegExp, string]");
            })();
            return s => s.replace(args[0], args[1]);

          case "normalizeDot":
            return s =>
              s.replace(/[.\uFF0E\u30FB\u00B7\u2219]/g, normalizedDotCharacter ?? "・");

          case "doubleConsonantToSmallTsu":
            return s =>
              s.replace(
                /([bcdfghjklmpqrstvwxyz])\1(?=[aeiouy])/g,
                "っ"
              );

          case "normalizeN":
            return s =>
              s
                .replace(/n'/g, "ん")
                .replace(/nn/g, "ん");

          case "romajiToHiragana":
            return s => {
              let out = s.toLowerCase();
              const keys = Object.keys(HIRAGANA_TABLE)
                .sort((a, b) => b.length - a.length);

              for (const k of keys) {
                out = out.replace(new RegExp(k, "g"), HIRAGANA_TABLE[k]);
              }
              return out;
            };

          case "hiraganaToKatakana":
            return s =>
              s.replace(/[\u3040-\u309F]/g, c =>
                String.fromCharCode(c.charCodeAt(0) + 0x60)
              );

          case "filterInvalidOnyomi":
            return (s) =>
              (!!s && /^[ァ-ヴー]+$/.test(s) && !s.includes(normalizedDotCharacter))
                ? s
                : '';


          case "filterInvalidKunyomi":
            return (s) =>
              (!!s && new RegExp(`^[ぁ-ゔー${normalizedDotCharacter}]+$`).test(s))
                ? s
                : '';

          default:
            debugLog("normalizeString_: unknown step type", step.type);
            throw new Error(`normalizeString_: unknown step "${step.type}"`);
        }
      });

      return ({value}) => {
        if (typeof value !== "string") return "";
        return pipeline.reduce((acc, fn) => fn(acc), value);
      };
    };

    const levenshteinCapped = (a, b, max = 1) => {
      if (a === b) return 0;
      if (Math.abs(a.length - b.length) > max) return max + 1;

      const n = a.length;
      const m = b.length;

      let prev = new Array(m + 1);
      let curr = new Array(m + 1);

      for (let j = 0; j <= m; j++) prev[j] = j;

      for (let i = 1; i <= n; i++) {
        curr[0] = i;
        let rowMin = curr[0];

        for (let j = 1; j <= m; j++) {
          const cost = a[i - 1] === b[j - 1] ? 0 : 1;
          curr[j] = Math.min(
            prev[j] + 1,
            curr[j - 1] + 1,
            prev[j - 1] + cost
          );
          if (curr[j] < rowMin) rowMin = curr[j];
        }

        if (rowMin > max) return max + 1;

        [prev, curr] = [curr, prev];
      }

      return prev[m];
    };

    const registry = {
      normalizeString_: normalizeString_, // !!!! Must stay as const to be used by the others during init.
      matchExact_: ({ dependencies, config }) => {
        debugLog("Registering matcher: matchExact", config);
        const normalizeInput =
          normalizeString_({ config: config.normalizeInput });
        const normalizeCandidate =
          normalizeString_({ config: config.normalizeCandidate });

        return ({value, candidates}) => {
          if (!Array.isArray(candidates) || candidates.length === 0) return null;
          if (!value) return null;

          const input = normalizeInput({value});
          if (!input) return null;

          for (const candidate of candidates) {
            if (input === normalizeCandidate({value: candidate})) {
              return candidate;
            }
          }

          return null;
        };
      },
      matchFuzzy_: ({ dependencies, config }) => {
        (config &&
          Number.isInteger(config.maxDistance) &&
          config.maxDistance >= 0 &&
          typeof config.enableTokenMatch === "boolean") ||
        (() => {
          debugLog("Registering matcher: matchFuzzy", config);
          throw new Error("matchFuzzy_: invalid config");
        })();

        const { maxDistance, enableTokenMatch } = config;

        const normalizeInput =
          normalizeString_({ config: config.normalizeInput });
        const normalizeCandidate =
          normalizeString_({ config: config.normalizeCandidate });

        return ({value, candidates}) => {
          if (!Array.isArray(candidates) || candidates.length === 0) return null;
          if (!value) return null;

          const input = normalizeInput({value});
          if (!input) return null;

          for (const candidate of candidates) {
            const target = normalizeCandidate({value: candidate});

            // direct fuzzy match
            if (levenshteinCapped(input, target, maxDistance) <= maxDistance) {
              return candidate; // ← canonical match
            }

            if (!enableTokenMatch) continue;

            const inTokens = input.split(" ").filter(Boolean);
            const caTokens = target.split(" ").filter(Boolean);

            if (!inTokens.length || !caTokens.length) continue;

            const tokenMatch = inTokens.every(it =>
              caTokens.some(mt =>
                levenshteinCapped(it, mt, maxDistance) <= maxDistance
              )
            );

            if (tokenMatch) {
              return candidate; // ← canonical match
            }
          }

          return null;
        };
      },
    };

    saneLog("UserInputsModule registry", Object.keys(registry));
    return { UserInputsModule: { registry } };
  })();
  //endregion

  //region InterceptionModule
  const InterceptionsModule = (() => {
    saneLog("Init InterceptionsModule");

    const bind_ = ({ dependencies, config }) => {
      if (!config || typeof config !== "object") {
        saneLog("InterceptionsModule.bind_: invalid config", config);
        throw new Error("InterceptionsModule.bind_: config object required");
      }

      const {
        target = document,
        boundaries,
        action,
        actionArgs,
        fire = "once",
        options = true,
      } = config;
      const resolvedAction = dependencies.ServiceActionsModule?.[action];
      const describeBoundaryConfig = dependencies.ServiceBoundariesModule.describeBoundaryConfig;

      if (!boundaries || typeof boundaries !== "object") {
        debugLog("InterceptionsModule.bind_: invalid boundaries", boundaries);
        throw new Error("InterceptionsModule.bind_: boundaries object required");
      }

      if (!["once", "every", "all"].includes(fire)) {
        debugLog("InterceptionsModule.bind_: invalid fire mode", fire);
        throw new Error(`InterceptionsModule.bind_: invalid fire mode "${fire}"`);
      }


      if (typeof resolvedAction !== "function") {
        debugLog("InterceptionsModule.bind_: unknown action", action);
        debugLog("Resolved to ", resolvedAction);
        throw new Error(`InterceptionsModule.bind_: unknown action "${action}"`);
      }

      /* Resolve boundaries from dependencies */
      const resolvedBoundaries = Object.fromEntries(
        Object.entries(boundaries).map(([eventType, boundariesNames]) => {
          if (!Array.isArray(boundariesNames) || boundariesNames.length === 0) {
            debugLog("InterceptionsModule.bind_: invalid boundary list", eventType, boundariesNames);
            throw new Error(`InterceptionsModule.bind_: boundaries[${eventType}] must be non-empty array`);
          }

          const resolved = boundariesNames.map(name => {
            const boundary =
              dependencies.ServiceBoundariesModule?.[name];

            if (typeof boundary !== "function") {
              debugLog("InterceptionsModule.bind_: unknown boundary", name);
              throw new Error(`InterceptionsModule.bind_: unknown boundary "${name}"`);
            }
            return boundary;
          });

          return [eventType, resolved];
        })
      );

      debugLog(
        "InterceptionsModule.bind_: resolved",
        { action, fire, boundaries: Object.keys(resolvedBoundaries) }
      );

      /* ===== Side-effect: bind listeners ===== */

      Object.entries(resolvedBoundaries).forEach(([eventType, boundaryList]) => {
        target.addEventListener(
          eventType,
          (e) => {
            const normalizedResults = [];

            for (const boundary of boundaryList) {
              const res = boundary(e);
              if (res) {
                normalizedResults.push({
                  originalEvent: res.originalEvent || e,
                  normalizedValue: res.normalizedValue,
                });
              }
            }
            if (fire === "once") {
              if (normalizedResults.length >= 1) {
                resolvedAction({...normalizedResults[0], ...actionArgs});
              }
              return;
            }

            if (fire === "every") {
              for (const context of normalizedResults) {
                resolvedAction({...context, ...actionArgs});
                if (e.cancelBubble) break;
              }
              return;
            }

            if (fire === "all") {
              if (normalizedResults.length === boundaryList.length) {
                resolvedAction({...normalizedResults[0], ...actionArgs});
              }
            }
          },
          options
        );

        saneLog("Interception bound", {
          action,
          fire,
          capture: !!options,
          boundaries: Object.fromEntries(
            Object.entries(boundaries).map(([eventType, names]) => [
              eventType,
              names.map(name => describeBoundaryConfig(name))
            ])
          )
        });


      });

      /* Return value is intentionally inert */
      return () => {};
    };

    const registry = {
      bind_: bind_,
    };

    saneLog("InterceptionsModule registry", Object.keys(registry));

    return { InterceptionsModule: { registry } };
  })();
  //endregion

  //region StateModule
  const StateModule = (() => {
    saneLog("Init StateModule");
    const registry = {
      getState_: ({ dependencies, config }) => {
        debugLog("Registering action: getState", config);
        const { serializableState, predicate, predicateArgs } = config;

        return (state) => predicate({serializableState, state, ...predicateArgs });
      },
      clearCurrentCardUserState_: ({ dependencies, config }) => {
        const { cleanCardUserState } = config;

        return ({ context }) => ({
          ...context,
          currentCardUserState : {...cleanCardUserState }
        })

      },
      commitState_: ({ dependencies, config }) => {
        const { serializableState, predicate, predicateArgs } = config;
        return (state) => predicate({serializableState, state, ...predicateArgs });
      }
    };

    saneLog("StateModule registry", Object.keys(registry));
    return { StateModule: { registry } };
  })();
  //endregion

  //region Kitsun
  //region KitsunBoundariesModule
  const KitsunBoundariesModule = (() => {
    saneLog("Init ServiceBoundariesModule (Kitsun)");
    const registry = {
      describeBoundaryConfig_: ({ }) => {
        debugLog("Registering boundary helper: describeBoundaryConfig");
        const moduleConfiguration = CONFIGURATION.modules.ServiceBoundariesModule;
        return (name) => {
          const boundaryConfig = moduleConfiguration?.[name]?.config;
          if (!boundaryConfig) {
            debugLog("describeBoundaryConfig: unknown boundary", name);
            return { name, predicate: "<unknown>" };
          }

          return {
            name,
            predicate: boundaryConfig.predicate?.toString?.() ?? "<non-function>",
            predicateArgs: boundaryConfig.predicateArgs ?? {}
          };
        }
      } ,
      onElementClick_: ({ config: { predicate, predicateArgs = {} } }) => {
        debugLog("Instantiating boundary from factory onElementClick_", predicate?.toString?.(), predicateArgs);

        return e => {
          const matchedElement = e.target?.closest(predicateArgs.selector)
          return predicate(e, predicateArgs, matchedElement)
            ? { originalEvent: e, normalizedValue: matchedElement }
            : undefined;
        }
      },
      onKey_: ({ config: { predicate, predicateArgs = {} } }) => {
        debugLog("Instantiating boundary from factory onKey_", predicate?.toString?.(), predicateArgs);

        return e =>
          predicate(e, predicateArgs)
            ? { originalEvent: e, normalizedValue: e.target?.value }
            : undefined;
      },
      onEvent_: ({ config: { predicate, predicateArgs = {} } }) => {
        debugLog("Instantiating boundary from factory onEvent_", predicate?.toString?.(), predicateArgs);

        return e =>
          predicate(e, predicateArgs)
            ? { originalEvent: e, normalizedValue: e.detail }
            : undefined;
      },
      onClick_: ({ config: { predicate, predicateArgs = {} } }) => {
        debugLog("Instantiating boundary from factory onClick_", predicate?.toString?.(), predicateArgs);

        return e =>
          predicate(e, predicateArgs)
            ? { originalEvent: e, normalizedValue: undefined }
            : undefined;
      },
    };
    saneLog("ServiceBoundariesModule (Kitsun) registry", Object.keys(registry));
    return { ServiceBoundariesModule: { registry } };
  })();
  //endregion

  //region KitsunCapabilitiesModule
  const KitsunCapabilitiesModule = (() => {
    saneLog("Init ServiceCapabilitiesModule (Kitsun)");
    const registry = {
      handleXHR_: ({ dependencies, config: { routes } }) => {

        saneLog("Registering XHR handler", {
          routes: routes.map(r => ({
            handler: r.handler,
            predicate: r.predicate?.toString?.(),
            predicateArgs: r.predicateArgs
          }))
        });

        return (xhr) => {
          if (!xhr || !xhr.responseURL) {
            debugLog("handleXHR: ignored XHR without responseURL", xhr);
            return;
          }

          for (const { predicate, predicateArgs = {}, handler, handlerArgs = {} } of routes) {
            if (!predicate(xhr, predicateArgs)) continue;

            const fn = dependencies.ServiceCapabilitiesModule?.[handler];
            if (typeof fn !== "function") {
              debugLog("handleXHR: handler not found", handler);
              return;
            }

            debugLog("handleXHR: route matched", {
              url: xhr.responseURL,
              handler,
              handlerArgs
            });

            fn(xhr, handlerArgs);
            return;
          }

          debugLog("handleXHR: no route matched", xhr.responseURL);
        }},
      emitReviewsLoaded_: ({ config }) => {
        saneLog("Registering capability: emitReviewsLoaded", config);

        return (xhr) => {
          let payload;
          try {
            payload = JSON.parse(xhr.responseText);
          } catch {
            debugLog("emitReviewsLoaded: invalid JSON payload");
            return;
          }

          const list = payload?.[config.listKey];
          if (!Array.isArray(list)) {
            debugLog("emitReviewsLoaded: missing or invalid list", config.listKey);
            return;
          }

          debugLog("emitReviewsLoaded: dispatching event", {
            eventName: config.eventName,
            count: list.length
          });

          document.dispatchEvent(
            new CustomEvent(config.eventName, {
              bubbles: true,
              composed: true,
              detail: { [config.detailKey]: list }
            })
          );
        };
      },
      emitReviewResolved_: ({ config }) => {
        saneLog("Registering capability: emitReviewResolved", config);

        return (_xhr, { outcome }) => {
          debugLog("emitReviewResolved: dispatching event", {
            eventName: config.eventName,
            outcome
          });

          document.dispatchEvent(
            new CustomEvent(config.eventName, {
              bubbles: true,
              composed: true,
              detail: { outcome }
            })
          );
        };
      },
      setContextCurrentCard_: ({ config }) => {
        debugLog("Registering capability: setContextCurrentCard", config);
        const mapperArgs = config.mapperArgs ?? {};
        const mapper = config.mapper;

        return ({ context }) => {
          const el = document.querySelector(config.keySelector);
          if (!el) {
            actionDebugLog("setContextCurrentCard: key element not found", config.keySelector);
            return { ...context, currentCard: null };
          }

          const key = el.textContent.trim();
          const review = context.reviews.get(key);
          if (!review) {
            actionDebugLog("setContextCurrentCard: review not found for key", key);
            return { ...context, currentCard: null };
          }

          actionDebugLog("setContextCurrentCard found review, raw", review);
          return { ...context, currentCard: mapper(review, mapperArgs)};
        };
      },
      indexReviews_: ({ dependencies, config }) => {
        saneLog("Registering capability: indexReviews", config);
        const { extractIndex, extractIndexArgs } = config;

        return ({actions, reviews, context}) => {
          context.reviews = new Map();
          for (const review of reviews) {
            const key = extractIndex(review, extractIndexArgs);
            context.reviews.set(key, review);
          }

          return context;
        };
      },
      submitAnswer_: ({ dependencies, config }) => {
        saneLog("Registering capability: submitAnswer", config);

        return async ({ answer }) => {
          const btn = config.getButton();
          const input = config.getInput();
          if (!btn) { debugLog("submitAnswer: submit button not found"); return; }
          if (!input) { debugLog("submitAnswer: input element not found"); return; }
          input.value = answer !== '' ? answer : "empty_string"
          btn.click();
          saneLog("Clicked on the submit button, input value is", input.value)
        };
      },
      clearAnswer_: ({ dependencies }) => () => {
        const input = document.getElementById("typeans");
        if (!(input instanceof HTMLInputElement)) return;

        input.classList.remove("tm-fade-out");
        input.classList.add("tm-fade-out");

        setTimeout(() => {
          input.value = "";
          input.classList.remove("tm-fade-out");
        }, 333);
      },
    }
    saneLog("ServiceCapabilitiesModule (Kitsun) registry", Object.keys(registry));
    return { ServiceCapabilitiesModule: { registry } };
  })();
  //endregion

  //region KitsunRendersModule
  const KitsunRendersModule = (() => {
    saneLog("Init ServiceRenderModule (Kitsun)");

    const playAnimation_ = ({ dependencies }) => (el, animation) => {
      if (!el || !animation) return;
      const { keyframes, options } = animation;
      el.animate(keyframes, options);
    };


    const registry = {
      playAnimation_: ({ dependencies, config }) =>  playAnimation_({ dependencies, config }),
      renderMeaningConfirmation_: ({ dependencies, config }) => {
        const { style } = config;

        return ({actions, context}) => {
          const el = document.getElementById("tm-meaning-confirm");
          if (!el) return;

          if (style.minHeight) el.style.minHeight = style.minHeight;

          if (context.currentCardUserState.meaningConfirmed) {
            el.textContent = context.currentCard.meanings.join(" ・ ");
            if (style.animation?.show) actions.playAnimation(el, style.animation.show);
          } else {
            el.textContent = "";
            el.style.opacity = 0;
          }
        };
      },
      renderRequirements_: ({ dependencies, config }) => {
        const { style } = config;

        return ({actions, context}) => {
          const titleEl = document.getElementById("tm-requirements-title");
          const bodyEl = document.getElementById("tm-requirements");
          if (!titleEl || !bodyEl) return;

          titleEl.textContent =
            `${context.currentCard.kanji} – Level ${context.currentCard.srs} – Requirements`;

          bodyEl.innerHTML = actions.rules.map(rule => rule({context}))
            .filter(r => r.ruleApplies)
            .map(r => `
              <div class="tm-requirement ${r.rulePass === true ? "ok" : "pending"}">
                ${r.message}
              </div>
            `)
            .join("");

          if (style?.animation?.update) {
            actions?.playAnimation(bodyEl, style.animation.update);
          }
        };
      },
      /*renderReadingPanel_: ({ dependencies, config }) => {
        const { style } = config;
        let prevOn = 0;
        let prevKun = 0;

        return ({ actions, context }) => {
          const onEl = document.getElementById("tm-on-used");
          const kunEl = document.getElementById("tm-kun-used");
          if (!onEl || !kunEl) return;

          const onNow = context.currentCardUserState.onyomiUsed.size;
          const kunNow = context.currentCardUserState.kunyomiUsed.size;

          onEl.innerHTML = onNow
            ? [...context.currentCardUserState.onyomiUsed].join("<br>")
            : "—";

          kunEl.innerHTML = kunNow
            ? [...context.currentCardUserState.kunyomiUsed].join("<br>")
            : "—";

          if (style?.animation?.on && onNow > prevOn) {
            actions.playAnimation(onEl, style.animation.on);
          }

          if (style?.animation?.kun && kunNow > prevKun) {
            actions?.playAnimation(kunEl, style.animation.kun);
          }

          prevOn = onNow;
          prevKun = kunNow;
        };
      },*/
      renderReadingPanel_: ({ dependencies, config }) => {
        const { style, renderReading } = config;
        let prevOn = 0;
        let prevKun = 0;



        return ({ actions, context }) => {
          const onEl = document.getElementById("tm-on-used");
          const kunEl = document.getElementById("tm-kun-used");
          const onAvailEl = document.getElementById("tm-on-available");
          const kunAvailEl = document.getElementById("tm-kun-available");

          if (!onEl || !kunEl) return;

          /* === Available-domain counts === */
          if (onAvailEl) {
            const n = context.currentCard?.onyomiDomainCount ?? 0;
            onAvailEl.textContent = n ? `(${n})` : "";
          }

          if (kunAvailEl) {
            const n = context.currentCard?.kunyomiDomainCount ?? 0;
            kunAvailEl.textContent = n ? `(${n})` : "";
          }

          /* === Used readings === */
          const onNow = context.currentCardUserState.onyomiUsed.size;
          const kunNow = context.currentCardUserState.kunyomiUsed.size;

          onEl.innerHTML = onNow
            ? [...context.currentCardUserState.onyomiUsed]
              .map(r => renderReading(context.currentCard.kanji, r))
              .join("  ")
            : "—";

          kunEl.innerHTML = kunNow
            ? [...context.currentCardUserState.kunyomiUsed]
              .map(r => renderReading(context.currentCard.kanji, r))
              .join("  ")
            : "—";

          /* === Animations === */
          if (style?.animation?.on && onNow > prevOn) {
            actions.playAnimation(onEl, style.animation.on);
          }

          if (style?.animation?.kun && kunNow > prevKun) {
            actions.playAnimation(kunEl, style.animation.kun);
          }

          prevOn = onNow;
          prevKun = kunNow;
        };
      },

      /*renderInput_: ({ dependencies, config }) => {
        const { style } = config;

        return ({ actions, context }) => {
          const input = document.getElementById("typeans");
          if (!input) return;

          const state = context.currentCardUserState;

          input.disabled = state.submissionLocked;


          const RULES = [
            {
              when: s => s.submissionLocked && !s.cardLevelCleared,
              text: `${context.normalizedValue} is wrong ! Go next ?`
            },
            {
              when: s => !s.submissionLocked && !s.cardLevelCleared,
              text: "Enter a reading or meaning to continue."
            },
            {
              when: s => s.submissionLocked && s.cardLevelCleared,
              text: `${context.normalizedValue} is wrong ! Still, level cleared ~ Go next.`
            },
            {
              when: s => !s.submissionLocked && s.cardLevelCleared,
              text: "Level cleared ~ feel free to try more."
            }
          ];

          input.placeholder =
            RULES.find(r => r.when(state))?.text ?? "";

          if (style?.animation?.update)
            actions?.playAnimation(input, style.animation.update);
        };
      }*/
      renderInput_: ({ dependencies, config }) => {
        const { style, renderOnPreview, renderKunPreview  } = config;

        return ({ actions, context }) => {
          const input = document.getElementById("typeans");
          if (!input) return;

          const onPreviewEl = document.querySelector(".tm-preview-on");
          const kunPreviewEl = document.querySelector(".tm-preview-kun");
          renderOnPreview(onPreviewEl, context.clearPreview ? `` : actions.toOnyomi({value: context.normalizedValue}));
          renderKunPreview(kunPreviewEl, context.clearPreview ? `` : actions.toKunyomi({value: context.normalizedValue}));
          context.clearPreview = false;
          const state = context.currentCardUserState;

          /* === input state === */
          input.disabled = state.submissionLocked;

          const RULES = [
            {
              when: s => s.submissionLocked && !s.cardLevelCleared,
              text: `${context.normalizedValue} is wrong ! Go next ?`
            },
            {
              when: s => !s.submissionLocked && !s.cardLevelCleared,
              text: "Enter a reading or meaning to continue."
            },
            {
              when: s => s.submissionLocked && s.cardLevelCleared,
              text: `${context.normalizedValue} is wrong ! Still, level cleared ~ Go next.`
            },
            {
              when: s => !s.submissionLocked && s.cardLevelCleared,
              text: "Level cleared ~ feel free to try more."
            }
          ];

          input.placeholder =
            RULES.find(r => r.when(state))?.text ?? "";

          /* === animation === */
          /*if (style?.animation?.update) {
            actions?.playAnimation(input, style.animation.update);
          }*/
        };
      }
    };

    saneLog("ServiceRenderModule (Kitsun) registry", Object.keys(registry));

    return { ServiceRendersModule: { registry } };
  })();
  //endregion

  //region KitsunActionsModule
  const KitsunActionsModule = (() => {
    saneLog("Init ServiceActionsModule (Kitsun)");
    const runPredicateGraph = async ({entrypoint, graph, context, actions}) => {
      actionDebugLog("runPredicateGraph", {entrypoint, graph, context, actions})
      let mutatedContext = context;
      let nodeKey = entrypoint;

      while (nodeKey) {
        const node = graph.nodes[nodeKey];
        if (!node) {
          throw new Error(`Unknown node "${nodeKey}"`);
        }

        actionDebugLog("Enter node", nodeKey);

        const exec = () =>
          node.run({ actions, context: mutatedContext, args: node.args });

        let result = exec();
        actionDebugLog("Node result", result);
        if (!result) {
          actionDebugLog("Node returned no result, stopping execution", nodeKey);
          break;
        }

        if (node.retry) {
          actionDebugLog("Node present a retry predicate", { node: nodeKey });
          const { attempts , delay, predicate } = node.retry;

          for (let i = 1; i < attempts; i++) {
            if (!predicate({ context: result.context }))
              break;
            actionDebugLog("Retry predicate matched, retrying", { node: nodeKey, attempts, delay });
            await new Promise(r => setTimeout(r, delay));
            result = exec();
          }
        }

        const { next, context } = result;

        actionDebugLog("Transition", { from: nodeKey, to: next, context: context });

        if (node.end === true) {
          actionSaneLog("Reached marked terminal node", nodeKey);
          break;
        }

        if (!next) {
          actionDebugLog("No next node, stopping by default");
          break;
        }

        mutatedContext = context
        nodeKey = next;
      }

      actionDebugLog("runPredicateGraph final context", mutatedContext)
      return mutatedContext;
    };
    const runGraph_ = ({ dependencies, config }) => {
      const {
        RulesModule,
        UserInputsModule,
        StateModule,
        ServiceCapabilitiesModule,
        ServiceRendersModule,
      } = dependencies;

      const rules = Object.values(RulesModule);

      const {
        inputMatchReading,
        inputMatchMeaning,
        toKunyomi,
        toOnyomi,
      } = UserInputsModule;

      const {
        getState,
        commitState,
        clearCurrentCardUserState
      } = StateModule;

      const {
        setContextCurrentCard,
        indexReviews,
        submitAnswer,
        clearAnswer,
      } = ServiceCapabilitiesModule;

      const {
        playAnimation,
        renderMeaningConfirmation,
        renderReadingPanel,
        renderRequirements,
        renderInput,
      } = ServiceRendersModule;

      const actions = {
        getState,
        commitState,
        clearCurrentCardUserState,
        setContextCurrentCard,
        submitAnswer,
        clearAnswer,
        indexReviews,
        inputMatchReading,
        inputMatchMeaning,
        toKunyomi,
        toOnyomi,
        playAnimation,
        renderMeaningConfirmation,
        renderReadingPanel,
        renderRequirements,
        renderInput,
        rules,
      };

      actionSaneLog("handleAnswer behavior graph", config)
      actionSaneLog("handleAnswer available actions in nodes", Object.keys(actions))

      return async ({ originalEvent, normalizedValue, entrypoint }) => {

        const deserializedContext = actions.getState();
        const contextWithInput = { ...deserializedContext, originalEvent, normalizedValue };
        actionSaneLog("context with input from event on entering graph", contextWithInput);
        const graphExecutionResult = await runPredicateGraph({
          actions: actions,
          graph:config.graph,
          context: contextWithInput,
          entrypoint: entrypoint
        });

        actions.commitState(graphExecutionResult);
        actionSaneLog("state on exiting graph", actions.getState());
      };
    };

    const registry = {
      runGraph_: ({ dependencies, config }) => runGraph_({ dependencies, config })
    };

    saneLog("ServiceActionsModule (Kitsun) registry", Object.keys(registry));
    return { ServiceActionsModule: { registry } };
  })();
  //endregion

  //region KitsunStyleModule
  const KitsunStyleModule = (() => {
    saneLog("Init ServiceStyleModule (Kitsun)");
    GM_addStyle(`
  .tm-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  grid-template-rows: auto auto;
  gap: 24px;
  width: 80%;
  margin: 0 auto;
}

.tm-card {
  background: var(--light);
  border-radius: 8px;
  padding: 12px 16px;
  box-shadow: var(--cardshadow);
  display: flex;
  flex-direction: column;
  justify-content: center;
  min-height: 96px;
}

.tm-on-card .tm-card-body,
.tm-kun-card .tm-card-body {
  flex-direction: row;
  flex-wrap: wrap;
}

/* Make input card visually consistent */
.tm-input-card {
  align-items: center;
  .tm-input-card {
  transition: box-shadow 120ms ease;
}

}

.tm-input-card:focus-within {
  box-shadow: 0 0 0 4px rgba(100, 180, 255, 0.15);
}
.tm-card-title {
  font-size: 0.85rem;
  color: var(--alt);
  margin-bottom: 6px;
}

.tm-card-body {
  font-size: 1.05rem;
  color: var(--dark-text);
  display: flex;
  flex-direction: column;
  gap: 6px;
}

/* === Requirement lines === */
.tm-requirement {
  display: flex;
  gap: 6px;
  font-size: 0.95rem;
  align-items: center;
}

/* Bullet */
.tm-requirement::before {
  content: "•";
  opacity: 0.7;
}

/* States */
.tm-requirement.ok {
  color: var(--success);
}

.tm-requirement.fail {
  color: var(--danger);
}

.tm-requirement.pending {
  color: var(--alt);
  font-style: italic;
  opacity: 0.85;
}

.tm-grid > .tm-card {
  align-self: stretch;
}

.tm-meaning-confirm {
  margin-top: -1.5rem;
  margin-bottom: 1.5rem;
  font-size: 1.2rem;
  color: var(--alt);
  opacity: 0.85;
  text-align: center;
  min-height: 1.6em;
  opacity: 0;
  transition: opacity 160ms ease-out;
}

.tm-meaning-confirm.is-visible {
  opacity: 0.85;
}


#typeans_wrapper {
  width: 100% !important;
}

/* Fade-out input when cleared */
@keyframes tm-fade-out {
  from { opacity: 1; }
  to   { opacity: 0; }
}

@keyframes tm-fade-in {
  from { opacity: 0; transform: translateY(-4px); }
  to   { opacity: 1; transform: translateY(0); }
}

.tm-fade-out {
  animation: tm-fade-out 333ms ease-out forwards;
}

.tm-fade-in {
  animation: tm-fade-in 160ms ease-out;
}

#typeans {
  caret-color: transparent;
  transition: border-color 120ms ease, box-shadow 120ms ease;
}

#typeans {
  transition:
    border-color 120ms ease,
    box-shadow 120ms ease,
    background-color 120ms ease;
}

#typeans:focus {
  outline: none;
  border-color: rgba(100, 180, 255, 0.6);
  box-shadow: none;
}

/* Disabled / submission locked */
#typeans:disabled {
  background-color: rgba(0, 0, 0, 0.02);
  border-color: rgba(180, 180, 180, 0.6);
  box-shadow: 0 0 0 2px rgba(180, 180, 180, 0.15);
  cursor: not-allowed;
}

/* Prevent focus highlight when disabled */
#typeans:disabled:focus {
  box-shadow: 0 0 0 2px rgba(180, 180, 180, 0.15);
}

.tm-card .tm-kanji {
  font-size: 1em;
}

.tm-card ruby {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  line-height: 1.6;
}
.tm-card rt,
.tm-card .tm-furigana {
  visibility: visible !important;
  color: var(--dark-text) !important;
  display: block !important;
  font-size: 0.75em !important;
  line-height: 1 !important;
  opacity: 0.55 !important;
  user-select: none !important;
}


.tm-reading-container {
  display: flex;
  flex-direction: row;
  gap: 16px;                 /* space between reading blocks */
  padding: 6px 8px;
}

.tm-reading-container > * {
  padding: 6px 8px;
  border-radius: 6px;
}


  `);
    saneLog("ServiceStyleModule loaded");
    return {};
  })();
  //endregion

  //endregion

  const service = bindModules_({
    ...RulesModule,
    ...UserInputsModule,
    ...StateModule,
    ...KitsunBoundariesModule ,
    ...KitsunCapabilitiesModule,
    ...KitsunRendersModule,
    ...KitsunActionsModule,
    ...InterceptionsModule,
    ...KitsunStyleModule,
  })(CONFIGURATION);

  //region utils
  const graphToMermaid = (graph) => {
    const lines = ["flowchart TD"];
    const seenEdges = new Set();
    const terminalNodes = new Set();
    const entrypointNodes = new Set();

    const addEdge = (from, to, label) => {
      if (!to) return;
      const key = `${from}->${label ?? ""}->${to}`;
      if (seenEdges.has(key)) return;
      seenEdges.add(key);

      lines.push(
        label
          ? `  ${from} -->|${label}| ${to}`
          : `  ${from} --> ${to}`
      );
    };

    for (const [name, node] of Object.entries(graph.nodes)) {
      /* ===============================
       * Entrypoints
       * =============================== */
      if (node.entrypoint) {
        lines.push(`  ${name}`);
        entrypointNodes.add(name);

        if (node.args?.next) {
          addEdge(name, node.args.next);
        }
      }

      /* ===============================
       * Terminal nodes
       * =============================== */
      if (node.end === true) {
        terminalNodes.add(name);
      }

      /* ===============================
       * Simple linear transition
       * =============================== */
      if (node.args?.next) {
        addEdge(name, node.args.next);
      }

      /* ===============================
       * Branching paths
       * =============================== */
      if (node.args?.paths) {
        for (const { next } of node.args.paths) {
          addEdge(name, next);
        }
      }

      /* ===============================
       * Default branch
       * =============================== */
      if (node.args?.default) {
        addEdge(name, node.args.default);
      }

      /* ===============================
       * Success / pending split
       * =============================== */
      if (node.args?.onSuccess || node.args?.onPending) {
        if (node.args.onSuccess) addEdge(name, node.args.onSuccess);
        if (node.args.onPending) addEdge(name, node.args.onPending);
      }

      /* ===============================
       * Retry loop
       * =============================== */
      if (node.retry) {
        addEdge(name, name, "retry");
      }
    }

    /* ===============================
     * Styles
     * =============================== */
    if (entrypointNodes.size > 0 || terminalNodes.size > 0) {
      lines.push("");
    }

    if (entrypointNodes.size > 0) {
      lines.push("  classDef entrypoint fill:#e3f2fd,stroke:#1e88e5,stroke-width:2px;");
      for (const name of entrypointNodes) {
        lines.push(`  class ${name} entrypoint;`);
      }
    }

    if (terminalNodes.size > 0) {
      lines.push("  classDef terminal fill:#eeeeee,stroke:#333,stroke-width:1px;");
      for (const name of terminalNodes) {
        lines.push(`  class ${name} terminal;`);
      }
    }

    return lines.join("\n");
  };
  //endregion


  //region Service XHR HOOK
  (function (open) {
    const { ServiceCapabilitiesModule } = service;
    const { handleXHR } = ServiceCapabilitiesModule;
    XMLHttpRequest.prototype.open = function () {
      this.addEventListener("readystatechange", function () {
        if (this.readyState !== 4) return;

        handleXHR(this);

      });

      open.apply(this, arguments);
    };
  })(XMLHttpRequest.prototype.open);
  saneLog("Service XHR HOOK ~> OK")
  //endregion

  console.groupEnd();
  console.log("SaneSRSEngine Loaded, hooked service:", CONFIGURATION.service)
  console.groupCollapsed("Mermaid representation of the behavior graph");
  console.log(graphToMermaid(BEHAVIOR_GRAPH));
  console.groupEnd();
})();
