// Runs Gumbo the way nokogiri's ext/nokogiri/gumbo.c does and prints each tree, for generate.py.
// Input records: u32 context length, context ("" for a document, "svg:x" or "math:x" for a foreign
// element, else an HTML tag), u32 input length, input. Text under the document is dropped and
// adjacent text is merged, as libxml2 does when nokogiri builds its tree.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include "nokogiri_gumbo.h"

static void out_str(const char *s) { fputs(s, stdout); }

static void dump(const GumboNode *node, int depth) {
  const GumboVector *children = node->type == GUMBO_NODE_DOCUMENT ? &node->v.document.children : &node->v.element.children;
  int pending_text = 0;
  for (unsigned i = 0; i < children->length; i++) {
    const GumboNode *c = children->data[i];
    int is_text = c->type == GUMBO_NODE_TEXT || c->type == GUMBO_NODE_WHITESPACE;
    if (is_text) {
      if (node->type == GUMBO_NODE_DOCUMENT) continue;
      if (!pending_text) { printf("%*s\"", depth * 2, ""); pending_text = 1; }
      out_str(c->v.text.text);
      continue;
    }
    if (pending_text) { out_str("\"\n"); pending_text = 0; }
    switch (c->type) {
      case GUMBO_NODE_CDATA: printf("%*s<![CDATA[%s]]>\n", depth * 2, "", c->v.text.text); break;
      case GUMBO_NODE_COMMENT: printf("%*s<!-- %s -->\n", depth * 2, "", c->v.text.text); break;
      case GUMBO_NODE_ELEMENT: case GUMBO_NODE_TEMPLATE: {
        const char *ns = c->v.element.tag_namespace == GUMBO_NAMESPACE_SVG ? "svg " : c->v.element.tag_namespace == GUMBO_NAMESPACE_MATHML ? "math " : "";
        printf("%*s<%s%s>\n", depth * 2, "", ns, c->v.element.name);
        const GumboVector *attrs = &c->v.element.attributes;
        for (unsigned j = 0; j < attrs->length; j++) {
          const GumboAttribute *a = attrs->data[j];
          const char *p = a->attr_namespace == GUMBO_ATTR_NAMESPACE_XLINK ? "xlink " : a->attr_namespace == GUMBO_ATTR_NAMESPACE_XML ? "xml " : a->attr_namespace == GUMBO_ATTR_NAMESPACE_XMLNS ? "xmlns " : "";
          printf("%*s%s%s=\"%s\"\n", depth * 2 + 2, "", p, a->name, a->value);
        }
        dump(c, depth + 1);
        break;
      }
      default: break;
    }
  }
  if (pending_text) out_str("\"\n");
}

static char *read_record(FILE *f, uint32_t *len) {
  if (fread(len, 4, 1, f) != 1) return NULL;
  char *buf = malloc(*len + 1);
  if (*len && fread(buf, 1, *len, f) != *len) exit(2);
  buf[*len] = 0;
  return buf;
}

int main(int argc, char **argv) {
  FILE *f = fopen(argv[1], "rb");
  uint32_t ctx_len, len;
  char *ctx;
  while ((ctx = read_record(f, &ctx_len))) {
    char *data = read_record(f, &len);
    GumboOptions options = kGumboDefaultOptions;
    options.max_attributes = 400;
    options.max_errors = 0;
    options.max_tree_depth = 400;
    if (ctx_len) {
      options.fragment_namespace = GUMBO_NAMESPACE_HTML;
      char *colon = strchr(ctx, ':');
      if (colon) { options.fragment_namespace = ctx[0] == 's' ? GUMBO_NAMESPACE_SVG : GUMBO_NAMESPACE_MATHML; options.fragment_context = colon + 1; }
      else options.fragment_context = ctx;
      options.max_tree_depth++;
    }
    GumboOutput *output = gumbo_parse_with_options(&options, data, len);
    if (output->status != GUMBO_STATUS_OK) {
      printf("#error %s\n", gumbo_status_to_string(output->status));
    } else if (ctx_len) {
      dump(output->root, 0);
    } else {
      if (output->document->v.document.has_doctype) printf("<!DOCTYPE %s>\n", output->document->v.document.name);
      dump(output->document, 0);
    }
    fputs("#end\n", stdout);
    gumbo_destroy_output(output);
    free(ctx); free(data);
  }
  return 0;
}
