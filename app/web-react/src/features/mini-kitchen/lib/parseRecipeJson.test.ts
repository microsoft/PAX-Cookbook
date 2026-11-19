import { describe, expect, it } from 'vitest';
import { buildRecipeRequestBody } from './candidateToRecipeBody';
import { renderPaxCommand } from './commandRenderer';
import { createDefaultMiniKitchenRecipe } from './defaultRecipe';
import { buildFullPaxRecipeExport, serializeFullPaxRecipe } from './fullPaxRecipeExporter';
import { parseFullPaxRecipeJson } from './fullPaxRecipeImporter';
import { parsePaxCommand } from './paxCommandImporter';
import { parseRecipeJson } from './parseRecipeJson';
import { resolvePermissions } from './permissionsResolver';
import { buildLiteRecipeExport, serializeLiteRecipe } from './recipeExporter';
import { parseLiteRecipeJson } from './recipeImporter';
import { translateLiteRecipeToFullRecipe } from './translateLiteRecipeToFullRecipe';

describe('raw Recipe JSON', () => {
  it.each([
    '{"kind":"one","kind":"two"}',
    '{"recipe":{"name":"one","Name":"two"}}',
    String.raw`{"items":[{"name":"one","\u006eame":"two"}]}`,
  ])('refuses duplicates before either importer can inspect the envelope', (text) => {
    expect(() => parseRecipeJson(text)).toThrow('Duplicate Recipe property.');
    expect(parseFullPaxRecipeJson(text)).toMatchObject({ ok: false, reason: 'invalid' });
    expect(parseLiteRecipeJson(text)).toMatchObject({ ok: false });
  });

  it('keeps object scopes separate and preserves JSON data types', () => {
    const text = String.raw`{"items":[{"name":"{\"label\":7}"},{"Name":"second"}],"date":"2026-01-02T03:04:05Z","empty":[],"enabled":false}`;
    expect(parseRecipeJson(text)).toEqual({
      items: [{ name: '{"label":7}' }, { Name: 'second' }],
      date: '2026-01-02T03:04:05Z', empty: [], enabled: false,
    });
    expect(() => parseRecipeJson('{"items":[1,]}')).toThrow(SyntaxError);
  });

  it('keeps valid full and lite files compatible with save and export', () => {
    const state = createDefaultMiniKitchenRecipe();
    state.identity.name = 'Inert round trip';
    state.destinations.fact.path = 'C:\\Fixtures\\audit.csv';
    state.advanced.extraArguments = '-ResultSize 50';
    const full = buildFullPaxRecipeExport({ state });
    if (!full.ok || !full.recipe) throw new Error(full.error);
    const lite = buildLiteRecipeExport({ state, command: renderPaxCommand(state), permissions: resolvePermissions(state) });

    const fullText = serializeFullPaxRecipe(full.recipe);
    const liteText = serializeLiteRecipe(lite.recipe);
    for (const result of [parseFullPaxRecipeJson(fullText), parseLiteRecipeJson(liteText)]) {
      expect(result.ok).toBe(true);
      if (!result.ok) throw new Error('Recipe did not import.');
      const saved = buildRecipeRequestBody(translateLiteRecipeToFullRecipe(result.state));
      expect(saved.body).toMatchObject({ advanced: { extraArguments: '-ResultSize 50' }, identity: { name: 'Inert round trip' } });
      expect(buildFullPaxRecipeExport({ state: result.state }).ok).toBe(true);
    }
    const duplicateName = (text: string) => text.replace('"name": "Inert round trip"', String.raw`"\u006eame":"other","name":"Inert round trip"`);
    expect(duplicateName(fullText)).not.toBe(fullText);
    expect(duplicateName(liteText)).not.toBe(liteText);
    expect(parseFullPaxRecipeJson(duplicateName(fullText))).toMatchObject({ ok: false, reason: 'invalid' });
    expect(parseLiteRecipeJson(duplicateName(liteText))).toMatchObject({ ok: false });
  });

  it('keeps harmless pasted options as data in the builder', () => {
    const imported = parsePaxCommand('-ResultSize 50');
    expect(imported.state?.advanced.extraArguments).toBe('-ResultSize 50');
  });
});