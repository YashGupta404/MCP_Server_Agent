// A document returned for a record by the BFF /api/context endpoint.
export interface UcebDocument {
  docId: string;
  name: string;
  type?: string;
  attributes?: Record<string, string>;
}
