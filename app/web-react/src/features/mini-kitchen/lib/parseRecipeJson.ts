export function parseRecipeJson(text: string): unknown {
  const containers: (Set<string> | null)[] = [];
  const tokens = /[{}\[\]]|"(?:[^"\\]|\\[\s\S])*"/g;
  const propertySeparator = /[\t\n\r ]*:/y;

  for (const match of text.matchAll(tokens)) {
    const token = match[0];
    if (token === '{') containers.push(new Set());
    else if (token === '[') containers.push(null);
    else if (token === '}' || token === ']') containers.pop();
    else {
      const properties = containers.at(-1);
      propertySeparator.lastIndex = match.index + token.length;
      if (properties && propertySeparator.test(text)) {
        const key = (JSON.parse(token) as string).toUpperCase();
        if (properties.has(key)) throw new SyntaxError('Duplicate Recipe property.');
        properties.add(key);
      }
    }
  }

  return JSON.parse(text) as unknown;
}