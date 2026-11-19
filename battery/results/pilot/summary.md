# Testbatteri 2026-11-19 14.51

Graf `2012392` · prompt `v3` · OpenAi `gpt-4.1-mini` · temperatur 0 · 3 kørsler pr. scenarie · commit `18bc360`

Den simulerede borger bekræftede kladder, der passede til det forventede, og afviste resten, som en borger der ved, hvad den mente.

Automatiske tjek. Den endelige vurdering af svarene (korrekt, hallucineret osv.) kommer fra den blinde bedømmelse i `scoring.csv`.

| | DcrStrict | DcrGuided | LlmOnly |
|---|---|---|---|
| Kørsler | 114 | 114 | 114 |
| Automatisk bestået | 89% (102/114) | 89% (102/114) | 92% (105/114) |
| Korrekt afslag (uden for grafen) | 100% (24/24) | 100% (24/24) | 100% (24/24) |
| Afslag, hvor der fandtes et svar | 8% (6/75) | 8% (6/75) | 0% (0/75) |
| Nøgleord fundet | 93% (81/87) | 93% (81/87) | 90% (78/87) |
| DCR-tilstand som forventet | 97% (84/87) | 97% (84/87) | – |
| Formularværdier korrekte | 93% (42/45) | 93% (42/45) | – |
| Stabil på tværs af kørsler | 100% (38/38) | 100% (38/38) | 100% (38/38) |
| Kladder til bekræftelse | 266 | 265 | 0 |
| Fejltolkninger fanget af bekræftelsen | 26 | 25 | 0 |
| LLM-kald pr. scenarie (gns.) | 2 | 2 | 2 |
| Tokens pr. scenarie (gns.) | 712 | 854 | 3002 |
| Svartid pr. tur, ms (gns.) | 266 | 349 | 643 |
| Fejl | 0 | 0 | 0 |

## Automatisk bestået pr. kategori

| Kategori | DcrStrict | DcrGuided | LlmOnly |
|---|---|---|---|
| direkte | 100% (18/18) | 100% (18/18) | 100% (18/18) |
| omformuleret | 100% (21/21) | 100% (21/21) | 100% (21/21) |
| formular | 83% (15/18) | 83% (15/18) | 83% (15/18) |
| flere_svar | 75% (9/12) | 75% (9/12) | 50% (6/12) |
| overblik | 0% (0/3) | 0% (0/3) | 100% (3/3) |
| uden_for_grafen | 100% (18/18) | 100% (18/18) | 100% (18/18) |
| robusthed | 88% (21/24) | 88% (21/24) | 100% (24/24) |

## Ikke bestået automatisk

| Scenarie | Tilstand | Kørsel | Hvorfor |
|---|---|---|---|
| S16 | DcrStrict | 1 | afslog, men grafen har svaret |
| S16 | DcrStrict | 2 | afslog, men grafen har svaret |
| S16 | DcrStrict | 3 | afslog, men grafen har svaret |
| S16 | DcrGuided | 1 | afslog, men grafen har svaret |
| S16 | DcrGuided | 2 | afslog, men grafen har svaret |
| S16 | DcrGuided | 3 | afslog, men grafen har svaret |
| S19 | LlmOnly | 1 | mangler: noteret |
| S19 | LlmOnly | 2 | mangler: noteret |
| S19 | LlmOnly | 3 | mangler: noteret |
| S20 | LlmOnly | 1 | mangler: noteret |
| S20 | LlmOnly | 2 | mangler: noteret |
| S20 | LlmOnly | 3 | mangler: noteret |
| S21 | LlmOnly | 1 | mangler: noteret |
| S21 | LlmOnly | 2 | mangler: noteret |
| S21 | LlmOnly | 3 | mangler: noteret |
| S23 | DcrStrict | 1 | forkerte værdier: A9=Vejgade 1, 2100 København Ø |
| S23 | DcrStrict | 2 | forkerte værdier: A9=Vejgade 1, 2100 København Ø |
| S23 | DcrStrict | 3 | forkerte værdier: A9=Vejgade 1, 2100 København Ø |
| S23 | DcrGuided | 1 | forkerte værdier: A9=Vejgade 1, 2100 København Ø |
| S23 | DcrGuided | 2 | forkerte værdier: A9=Vejgade 1, 2100 København Ø |
| S23 | DcrGuided | 3 | forkerte værdier: A9=Vejgade 1, 2100 København Ø |
| S24 | DcrStrict | 1 | mangler: minSU |
| S24 | DcrStrict | 2 | mangler: minSU |
| S24 | DcrStrict | 3 | mangler: minSU |
| S24 | DcrGuided | 1 | mangler: minSU |
| S24 | DcrGuided | 2 | mangler: minSU |
| S24 | DcrGuided | 3 | mangler: minSU |
| S31 | DcrStrict | 1 | afslog, men grafen har svaret; mangler: tilbagevirkende; ikke udført: førstegang, A2 |
| S31 | DcrStrict | 2 | afslog, men grafen har svaret; mangler: tilbagevirkende; ikke udført: førstegang, A2 |
| S31 | DcrStrict | 3 | afslog, men grafen har svaret; mangler: tilbagevirkende; ikke udført: førstegang, A2 |
| S31 | DcrGuided | 1 | afslog, men grafen har svaret; mangler: tilbagevirkende; ikke udført: førstegang, A2 |
| S31 | DcrGuided | 2 | afslog, men grafen har svaret; mangler: tilbagevirkende; ikke udført: førstegang, A2 |
| S31 | DcrGuided | 3 | afslog, men grafen har svaret; mangler: tilbagevirkende; ikke udført: førstegang, A2 |
