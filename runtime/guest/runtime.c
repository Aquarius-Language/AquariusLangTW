/* Aquarius value ABI 2. Portable execution; only host services cross the ABI. */
#include <stdint.h>
#include <stddef.h>

typedef uint32_t u32;
typedef struct { u32 tag, ref; double number; } Value;
enum { VOID, INT, FLOAT, DOUBLE, BOOL, STRING, ARRAY, HASH, CLOSURE, MODULE, NATIVE, NULL_VALUE, BREAK_VALUE, ERROR, BUILTIN, STRUCT, STRUCT_TYPE };
/* FOREIGN blocks are never collected: memory reserved for the host (aqua_reserve) and pages another module of the same
   memory grew (web side modules' libc), which the heap steps over. */
enum { FREE, RAW, TEXT, VALUES, ENV, BINDINGS, CLOSURES, ARRAYS, HASHES, FRAMES, LOOPS, EXECUTIONS, PINS, FOREIGN, STRUCTS, SHAPES };
/* Blocks tile the heap contiguously from heap_start to heap_end; walk them by address. */
typedef struct { u32 size, reserved, kind, marked; } Block;
typedef struct { u32 length; uint16_t data[]; } Text;
typedef struct { Text *name; u32 hash; Value value; } Binding;
typedef struct Scope { struct Scope *outer; Binding *bindings; u32 count, capacity, captured; u32 *index,index_capacity; } Scope;
typedef struct { u32 references,capacity,used,padding; Value values[]; } ArrayStorage;
/* element: 0, or the one value tag a typed array (整數[] / int[] ...) holds; set when an annotation check accepts it. */
typedef struct Array { u32 count, capacity; Value *values; struct Array *dirty_next; u32 dirty, exposed; ArrayStorage *storage; u32 element; } Array;
typedef struct { Value key, value; } Pair;
/* A struct type (結構 / struct), made when its declaration runs: its program-wide id (the annotation code is
   32 | id << 6), name and fields, each with its annotation code (0: any value) and type name (for messages). */
typedef struct { Text *name; u32 code; Text *type; u32 pad; } ShapeField;
typedef struct { u32 id, count; Text *name; u32 pad; ShapeField fields[]; } Shape;
/* A struct value: its type and one value per field, in one block. */
typedef struct { Shape *shape; u32 count, pad0, pad1; Value fields[]; } Struct;
/* pairs[0..count) are dense in insertion order (hosts read them directly). Above HASH_INDEX_MIN pairs, index is an
   open-addressing table over the first `indexed` pairs (see hashes.h); pairs appended past it (by the host, or since the
   last lookup) are indexed lazily by the next lookup. */
typedef struct Hash { u32 count,capacity; Pair *pairs; struct Hash *dirty_next; u32 dirty,exposed; u32 *index,indexed; } Hash;
typedef struct { u32 table, arity, capacity, wrap_return; Text **parameters; Value *constants; u32 numeric_table; Text *recursive_name; u32 locals, flags; } Function;
/* Function flags. PROMOTED_SCOPE: parameters and function-level locals live in frame slots 0..locals-1;
   the frame's scope is the closure scope itself. Only closure-free bodies are promoted, so no other
   frame, closure or host can observe the replaced lexical scope. */
enum { PROMOTED_SCOPE=1, NATIVE_TIER=2 }; /* NATIVE_TIER: numeric_table is a native adapter, numeric_table+1 its resume adapter. */
/* Runtime-only speculation statistics in the upper bits of Function.flags (instance memory). */
#define NUMERIC_HITS(fn) (((fn)->flags>>16)&0xff)
#define NUMERIC_MISSES(fn) ((fn)->flags>>24)
#define UNDECLARED 0xffffffffu
#define HEAP_LIMIT (2048u*1024*1024) /* the guest heap's ceiling: the compiler declares a 2 GiB memory maximum */
typedef struct { u32 count; Function *functions; } Program;
/* cache: binding cache shared by promoted-scope calls of this closure (their frame scope is the closure scope). */
typedef struct Closure { Program *program; u32 function; Scope *scope; u32 context; u32 *cache, cache_era; } Closure;
typedef struct Loop { struct Loop *previous; Scope *outer; u32 base; Text *binding; } Loop;
typedef struct Execution Execution;
typedef struct Frame {
    struct Frame *parent; Execution *execution; Program *program; Function *function;
    Scope *scope; Loop *loops; u32 pc, sp, returned, context, capacity, pending_base, pending_count; u32 *binding_cache;
    /* native_site: where a deoptimized native (fully typed) function resumes natively when the scheduler runs this
       frame again (1 = entry, 2+ = a loop head); native_closure: its closure. */
    u32 native_site; struct Closure *native_closure; Value values[];
} Frame;
struct Execution { Execution *next; Frame *current; Value result; u32 budget, status; Function *numeric_depth_disabled; u32 poll; Closure *numeric_closure; Frame *numeric_innermost; };
_Static_assert(offsetof(Frame, values)==64, "Compiler frame ABI mismatch");
_Static_assert(offsetof(Execution, status)==28 && offsetof(Execution, budget)==24 && offsetof(Execution, poll)==36 && offsetof(Execution, numeric_closure)==40, "Compiler execution ABI mismatch");
_Static_assert(sizeof(Function)==40, "Function layout mismatch");
_Static_assert(sizeof(Value)==16 && sizeof(Binding)==24 && offsetof(Binding,value)==8, "Host value/binding ABI mismatch");
/* Every offset the native host and the C# emitters use is listed in layout.def and checked here by layout.h. */
/* Host synchronization block (see aqua_pending): host-visible work lists and the host environment era. */
typedef struct { Array *dirty_arrays; Hash *dirty_hashes; Array *released_arrays; Hash *released_hashes; u32 host_era; } Pending;
#include "layout.h"
typedef struct Pin { struct Pin *next, *prev; Value value; } Pin;

__attribute__((import_module("aquarius_v2"), import_name("service")))
extern int host_import(int operation, int context, int subject, int name, int arguments, int count, int output);
static int host_service(int operation, int context, int subject, int name, int arguments, int count, int output);

static u32 heap_start, heap_end, free_heads[26], free_mask; /* bit c: free_heads[c] is not empty */
/* End of the memory this heap grew. The bump allocator never passes it, so pages another module sharing this memory
   grew (the web host's libc) are stepped over instead of reused; see own_memory. */
static u32 heap_limit;
/* A free block being carved sequentially like the bump region; it is on no free list and always a valid FREE block. */
static Block *carving;
static Scope *builtin_scopes[65536];
static Scope *portable_modules[4]; /* Thread, Array, Hash, String: one module each, so every import is the same value */
static u32 builtin_contexts[65536], builtin_context_count;
static Execution *executions;
static Pin *pins;
static Value empty;
static u32 mark_pending;
static unsigned char native_live[65536];
static u32 marked_native_ids[65536], marked_native_count, collection_generation;
/* Net allocation since the last collection, and heap growth beyond the high-water mark (which explicit releases never
   offset: freed blocks of a growing array cannot serve its next, larger request until a collection coalesces them).
   Refilling space a collection returned to the bump allocator is not growth: that memory is already committed. */
static u32 bytes_since_collection, heap_growth, heap_high;
static int collection_due(void);
/* Collection is due after gc_threshold bytes of allocation that were not explicitly released.
   The threshold grows with the live heap so repeated collections of large live graphs stay amortized. */
static u32 gc_threshold=8u*1024*1024, gc_armed;
static u32 scope_epoch=1, epoch_era=1;
static u32 numeric_fast_calls, numeric_fallback_calls, numeric_depth_fallbacks, numeric_deopts;
u32 aqua_numeric_deopts(void) { return numeric_deopts; }
u32 aqua_numeric_fast_calls(void) { return numeric_fast_calls; }
u32 aqua_numeric_fallback_calls(void) { return numeric_fallback_calls; }
u32 aqua_numeric_depth_fallbacks(void) { return numeric_depth_fallbacks; }
u32 aqua_collect(void);

// Bulk-memory intrinsics avoid byte-by-byte copies and preserve overlapping moves.
void *memcpy(void *d, const void *s, size_t n) { __builtin_memcpy(d,s,n);return d; }
void *memset(void *d, int x, size_t n) { __builtin_memset(d,x,n);return d; }
void *memmove(void *d, const void *s, size_t n) { __builtin_memmove(d,s,n);return d; }
static Value value(u32 tag, u32 ref, double n) { return (Value){tag,ref,n}; }
static u32 address(const void *p) { return (u32)(uintptr_t)p; }
static void *pointer(u32 p) { return (void *)(uintptr_t)p; }
static u32 allocation_class(u32 size) { return 27u-(u32)__builtin_clz(size); }
static void push_free(Block*b) { u32 c=allocation_class(b->size);*(u32*)(b+1)=free_heads[c];free_heads[c]=address(b);free_mask|=1u<<c; }
static Block *pop_free(u32 c) { Block*b=pointer(free_heads[c]);free_heads[c]=*(u32*)(b+1);if(!free_heads[c])free_mask&=~(1u<<c);return b; }
static void release(void *p) { if(p&&address(p)>=heap_start){Block*b=(Block*)p-1;if(b->kind==FREE)return;bytes_since_collection=bytes_since_collection>b->size?bytes_since_collection-b->size:0;b->kind=FREE;push_free(b);} }
static void initialize_allocation(void *p,u32 n,u32 kind) {
    // Collection scans only committed elements and live stack operands.
    // Constructors fill value/binding buffers completely before a checkpoint.
    if(kind==VALUES||kind==BINDINGS)return;
    // Small fixed-size clears stay as stores: engines lower variable memory.fill to a runtime call.
    if(kind==FRAMES){memset(p,0,sizeof(Frame));return;}
    if(n<=64){u32*w=p;for(u32 i=0;i<n/4;i++)w[i]=0;return;}
    memset(p,0,n);
}
/* Allocation slow paths stay out of line so the common cases (an exact-size free block, or bumping the heap end)
   inline into the hot constructors. */
static void arm_collection(void) { if(!gc_armed&&collection_due()){gc_armed=1;for(Execution*e=executions;e;e=e->next)e->poll=1;} }
/* When another module sharing this memory grew it since the heap last did, its pages lie between heap_limit and the
   memory's end: cover them (and the heap's unused tail) with a FOREIGN block and continue the heap after them. The
   block's header goes at heap_end, so the heap never fills the last sizeof(Block) bytes below heap_limit: otherwise the
   header would overwrite the first bytes of the other module's pages (the web host's malloc, sharing the memory). */
static void own_memory(void) {
    u32 size=(u32)__builtin_wasm_memory_size(0)*65536;
    if(size==heap_limit)return;
    Block*b=pointer(heap_end);b->size=size-heap_end-sizeof(Block);b->reserved=0;b->kind=FOREIGN;b->marked=0;
    heap_end=heap_limit=size;
}
static __attribute__((noinline)) void *allocate_slow(u32 n,u32 bucket,u32 kind) {
    // Carve the largest free run before growing the heap: sequential allocation with no list operations.
    if(!carving||carving->size<n){u32 larger=free_mask&~((2u<<bucket)-1u);if(larger){if(carving)push_free(carving);carving=pop_free(31u-(u32)__builtin_clz(larger));}}
    if(carving&&carving->size>=n){
        Block*b=carving;u32 rest=b->size-n;carving=0;
        if(rest>=sizeof(Block)+16){Block*r=(Block*)((char*)(b+1)+n);r->size=rest-sizeof(Block);r->reserved=0;r->kind=FREE;r->marked=0;carving=r;b->size=n;}
        b->reserved=0;b->kind=kind;b->marked=0;initialize_allocation(b+1,b->size,kind);return b+1;
    }
    own_memory();
    if(n>HEAP_LIMIT-heap_end-sizeof(Block))__builtin_trap();
    u32 end=heap_end+sizeof(Block)+n;
    if(end+sizeof(Block)>heap_limit){if(__builtin_wasm_memory_grow(0,(end+sizeof(Block)-heap_limit+65535)/65536)==(size_t)-1)__builtin_trap();heap_limit=(u32)__builtin_wasm_memory_size(0)*65536;}
    if(end>heap_high){heap_growth+=end-(heap_high>heap_end?heap_high:heap_end);heap_high=end;arm_collection();}
    Block *b=pointer(heap_end);b->size=n;b->reserved=0;b->kind=kind;b->marked=0;heap_end=end;initialize_allocation(b+1,n,kind);return b+1;
}
static inline __attribute__((always_inline)) void *allocate(u32 n,u32 kind) {
    if(n>HEAP_LIMIT)__builtin_trap();
    n=n<=16?16:1u<<(32u-(u32)__builtin_clz(n-1));u32 bucket=allocation_class(n);
    bytes_since_collection+=n;
    // Compiled checkpoints poll Execution.poll instead of calling into the runtime every block.
    if(bytes_since_collection>=gc_threshold)arm_collection();
    if(free_mask>>bucket&1){Block*b=pop_free(bucket);b->reserved=0;b->kind=kind;b->marked=0;initialize_allocation(b+1,b->size,kind);return b+1;}
    // Bump directly while no free run could serve the request and the heap has room.
    if(!carving&&!(free_mask>>bucket)){u32 end=heap_end+sizeof(Block)+n;
        if(end+sizeof(Block)<=heap_limit&&n<=HEAP_LIMIT-heap_end-sizeof(Block)){if(end>heap_high){heap_growth+=end-(heap_high>heap_end?heap_high:heap_end);heap_high=end;if(heap_growth/2>=gc_threshold)arm_collection();}
            Block*b=pointer(heap_end);b->size=n;b->reserved=0;b->kind=kind;b->marked=0;heap_end=end;initialize_allocation(b+1,n,kind);return b+1;}}
    return allocate_slow(n,bucket,kind);
}
u32 aqua_initialize(u32 start) { if(!heap_start){heap_start=heap_end=(start+15)&~15u;heap_limit=(u32)__builtin_wasm_memory_size(0)*65536;}return heap_start; }
u32 aqua_allocate(u32 n) { return address(allocate(n,RAW)); }
/* Memory the host keeps for the life of the instance (the web host's module data and stacks): never collected or moved.
   Only after aqua_initialize: initialization starts the heap anew, which would then allocate over earlier reservations. */
u32 aqua_reserve(u32 n) { if(!heap_start)__builtin_trap();Block*b=(Block*)allocate(n,RAW)-1;b->kind=FOREIGN;bytes_since_collection=bytes_since_collection>b->size?bytes_since_collection-b->size:0;return address(b+1); }
u32 aqua_heap_bytes(void) { return heap_end-heap_start; }
u32 aqua_heap_free(void) { u32 n=0;for(u32 a=heap_start;a<heap_end;a+=sizeof(Block)+((Block*)pointer(a))->size)if(((Block*)pointer(a))->kind==FREE)n+=((Block*)pointer(a))->size;return n; }
/* Net allocation leads; growth triggers only at twice the threshold, so it backs up net allocation when releases keep
   that low (a repeatedly regrown array) without collecting earlier than before in ordinary programs. */
static int collection_due(void) { return bytes_since_collection>=gc_threshold||heap_growth/2>=gc_threshold; }
u32 aqua_collection_due(void) { return (u32)collection_due(); }
u32 aqua_collection_generation(void) { return collection_generation; }
u32 aqua_value(void) { return address(allocate(sizeof(Value),RAW)); }
u32 aqua_text(u32 length) { Text *t=allocate(sizeof(Text)+length*2,TEXT);t->length=length;return address(t); }
/* Runtime messages are UTF-8 C strings (some are bilingual); texts are UTF-16. */
static Text *ascii(const char *s) {
    const unsigned char*u=(const unsigned char*)s;u32 n=0;
    for(u32 i=0;u[i];i++)if((u[i]&0xC0)!=0x80)n+=u[i]>=0xF0?2:1;
    Text*t=pointer(aqua_text(n));u32 o=0;
    for(u32 i=0;u[i];){u32 c=u[i++];
        if(c>=0xF0){c=(c&7)<<18|(u[i]&63)<<12|(u[i+1]&63)<<6|(u[i+2]&63);i+=3;c-=0x10000;t->data[o++]=(uint16_t)(0xD800|c>>10);t->data[o++]=(uint16_t)(0xDC00|(c&1023));continue;}
        if(c>=0xE0){c=(c&15)<<12|(u[i]&63)<<6|(u[i+1]&63);i+=2;}else if(c>=0xC0){c=(c&31)<<6|(u[i]&63);i++;}
        t->data[o++]=(uint16_t)c;}
    return t;
}
static Text *concat(Text *a,Text *b) { Text*t=pointer(aqua_text(a->length+b->length));memcpy(t->data,a->data,a->length*2);memcpy(t->data+a->length,b->data,b->length*2);return t; }
static int text_equal(Text *a,Text*b) { if(a==b)return 1;if(!a||!b||a->length!=b->length)return 0;for(u32 i=0;i<a->length;i++)if(a->data[i]!=b->data[i])return 0;return 1; }
static const char *type_name(u32 tag) { static const char *names[]={"NULL","INTEGER","FLOAT","DOUBLE","BOOLEAN","STRING","ARRAY","HASH","FUNCTION","MODULE","NATIVE","NULL","BREAK_OBJ","ERROR","BUILTIN","STRUCT","STRUCT_TYPE"};return tag<=STRUCT_TYPE?names[tag]:"UNKNOWN"; }
static void fail(Frame*f,Text*t) { f->execution->result=value(ERROR,address(t),0);f->execution->status=3; }
static void failure(Frame*f,const char*s) { fail(f,ascii(s)); }
static void named_failure(Frame*f,const char*s,Text*t) { fail(f,concat(ascii(s),t)); }
static void operator_failure(Frame*f,Value a,Value b,const char*op) {
    Text*t=ascii(a.tag==b.tag?"Unknown operator: ":"Type mismatch: ");t=concat(t,ascii(type_name(a.tag)));t=concat(t,ascii(" "));t=concat(t,ascii(op));t=concat(t,ascii(" "));fail(f,concat(t,ascii(type_name(b.tag))));
}
static Text *decimal(u32 n) { char b[11];u32 count=0;do{b[count++]=(char)('0'+n%10);n/=10;}while(n);Text*t=pointer(aqua_text(count));for(u32 i=0;i<count;i++)t->data[i]=b[count-i-1];return t; }
static Scope *scope(Scope *outer) { Scope*s=allocate(sizeof(Scope),ENV);s->outer=outer;return s; }
static void invalidate_scopes(void) { if(++scope_epoch)return;scope_epoch=1;epoch_era++;for(Execution*e=executions;e;e=e->next)for(Frame*f=e->current;f;f=f->parent)if(f->binding_cache)memset(f->binding_cache,0,(f->function->wrap_return>>1)*8); }
u32 aqua_scope(u32 outer) { Scope*s=scope(pointer(outer));s->captured=1;return address(s); }
/* FNV-1a over the code units, never 0. A heap text's hash is cached in its block header (Block.reserved, cleared by every
   allocation): texts are not changed after they are first used as names or keys. */
static u32 text_hash(Text*n) {
    Block*b=address(n)>=heap_start&&address(n)<heap_end?(Block*)n-1:0;if(b&&b->kind!=TEXT)b=0;if(b&&b->reserved)return b->reserved;
    u32 h=2166136261u;for(u32 i=0;i<n->length;i++)h=(h^n->data[i])*16777619u;h+=!h;if(b)b->reserved=h;return h;
}
static Binding *find_small_scope(Scope*s,Text*n) { for(u32 i=0;i<s->count;i++)if(text_equal(s->bindings[i].name,n))return s->bindings+i;return 0; }
static Binding *find_owned_hash(Scope*s,Text*n,u32 hash) { if(!s->index)return find_small_scope(s,n);u32 i=hash&(s->index_capacity-1);while(s->index[i]){Binding*b=s->bindings+s->index[i]-1;if(b->hash==hash&&text_equal(b->name,n))return b;i=(i+1)&(s->index_capacity-1);}return 0; }
static Binding *find_owned(Scope*s,Text*n) { return s->index?find_owned_hash(s,n,text_hash(n)):find_small_scope(s,n); }
static Binding *find(Scope*s,Text*n) { u32 hash=0,hashed=0;for(;s;s=s->outer){Binding*b;if(s->index){if(!hashed){hash=text_hash(n);hashed=1;}b=find_owned_hash(s,n,hash);}else b=find_small_scope(s,n);if(b)return b;}return 0; }
static void index_binding(Scope*s,u32 binding) { u32 i=s->bindings[binding].hash&(s->index_capacity-1);while(s->index[i])i=(i+1)&(s->index_capacity-1);s->index[i]=binding+1; }
static void rebuild_scope_index(Scope*s) { if(!s->index)return;memset(s->index,0,s->index_capacity*sizeof(u32));for(u32 i=0;i<s->count;i++){s->bindings[i].hash=text_hash(s->bindings[i].name);index_binding(s,i);} }
static void declare(Scope*s,Text*n,Value v) {
    Binding*b=find_owned(s,n);if(b){b->value=v;return;}
    invalidate_scopes();
    if(s->count==s->capacity){u32 cap=s->capacity?s->capacity*2:4;Binding*next=allocate(cap*sizeof(Binding),BINDINGS);memcpy(next,s->bindings,s->count*sizeof(Binding));release(s->bindings);s->bindings=next;s->capacity=cap;if(cap>4){release(s->index);s->index_capacity=cap*2;s->index=allocate(s->index_capacity*sizeof(u32),RAW);rebuild_scope_index(s);}}
    s->bindings[s->count]=(Binding){n,s->index?text_hash(n):0,v};if(s->index)index_binding(s,s->count);s->count++;
}
u32 aqua_scope_set(u32 s,u32 n,u32 v) { declare(pointer(s),pointer(n),*(Value*)pointer(v));return 0; }
u32 aqua_scope_assign(u32 s,u32 n,u32 v) { Binding*b=find(pointer(s),pointer(n));if(!b)return 0;b->value=*(Value*)pointer(v);return 1; }
u32 aqua_scope_count(u32 s) { return ((Scope*)pointer(s))->count; }
u32 aqua_scope_binding(u32 s,u32 i) { Scope*x=pointer(s);return i<x->count?address(x->bindings+i):0; }
u32 aqua_scope_get(u32 s,u32 n) { Binding*b=find(pointer(s),pointer(n));return b?address(&b->value):0; }
u32 aqua_scope_delete(u32 s,u32 n) { Scope*x=pointer(s);Binding*b=find_owned(x,pointer(n));if(!b)return 0;invalidate_scopes();u32 i=(u32)(b-x->bindings);memmove(b,b+1,(x->count-i-1)*sizeof(Binding));x->count--;rebuild_scope_index(x);return 1; }
static Value pop(Frame*f) { if(!f->sp){failure(f,"Invalid compiled operand stack.");return empty;}return f->values[--f->sp]; }
static void push(Frame*f,Value v) { if(v.tag==ERROR){f->execution->result=v;f->execution->status=3;return;}if(f->sp>=f->capacity){failure(f,"Compiled operand stack capacity exceeded.");return;}f->values[f->sp++]=v; }
static Value peek(Frame*f,u32 depth) { return f->sp>=depth?f->values[f->sp-depth]:empty; }
static int numeric(Value v) { return v.tag>=INT&&v.tag<=DOUBLE; }
/* Integer rules, identical on every target: integer + - * and negation wrap around (two's complement, like i32.add);
   integer / and % by zero are errors; converting a float or double to an integer truncates toward zero and saturates
   to the integer range, NaN giving 0 (i32.trunc_sat_f64_s). */
static double saturate_int(double n) { return n!=n?0:n<=-2147483648.0?-2147483648.0:n>=2147483647.0?2147483647.0:(double)(int32_t)n; }
static Value number(double n,u32 tag) { if(tag==INT)n=saturate_int(n);else if(tag==FLOAT)n=(double)(float)n;return value(tag,0,n); }
static Value integer(int64_t n) { return value(INT,0,(double)(int32_t)(uint32_t)(uint64_t)n); }
static Value successor(Value v) { return v.tag==INT?integer((int64_t)v.number+1):number(v.number+1,v.tag); }
/* Builtin ids of the conversions 整數(x)/int(x), 浮點數(x)/float(x), 雙精度(x)/double(x), in tag order. */
#define CONVERT_FIRST_ID 46u
static int equal(Value a,Value b,int typed) { if(numeric(a)&&numeric(b))return (!typed||a.tag==b.tag)&&(a.number==b.number||(typed&&a.number!=a.number&&b.number!=b.number));if(a.tag!=b.tag)return 0;if(a.tag==STRING)return text_equal(pointer(a.ref),pointer(b.ref));if(a.tag==BOOL)return a.number==b.number;return a.ref==b.ref; }
static Value binary(Frame*f,int op,Value a,Value b,int compound) {
    static const char *symbols[]={"+","-","*","/","<",">","<=",">=","==","!=","&&","||","%"};
    if(a.tag==INT&&b.tag==INT&&(op<=3||op==12)){
        int32_t x=(int32_t)a.number,y=(int32_t)b.number;
        if(op==0)return integer((int64_t)x+y);
        if(op==1)return integer((int64_t)x-y);
        if(op==2)return integer((int64_t)x*y);
        if(!y){failure(f,op==3?"Integer division by zero / 整數不能除以零。":"Integer remainder by zero / 整數不能對零取餘數。");return empty;}
        if(y==-1)return integer(op==3?-(int64_t)x:0);
        return integer(op==3?x/y:x%y);
    }
    if(numeric(a)&&numeric(b)){
        u32 tag=compound?a.tag:a.tag>b.tag?a.tag:b.tag;double x=a.number,y=b.number;
        switch(op){case 0:return number(x+y,tag);case 1:return number(x-y,tag);case 2:return number(x*y,tag);case 3:return number(x/y,tag);case 4:return value(BOOL,0,x<y);case 5:return value(BOOL,0,x>y);case 6:return value(BOOL,0,x<=y);case 7:return value(BOOL,0,x>=y);case 8:return value(BOOL,0,x==y);case 9:return value(BOOL,0,x!=y);
        case 12:
            // Remainder truncates toward zero like division; integer remainder by zero is an error.
            if(tag==INT&&y==0){failure(f,"Integer remainder by zero / 整數不能對零取餘數。");return empty;}
            return number(x-__builtin_trunc(x/y)*y,tag);}
    }
    if(op==8||op==9)return value(BOOL,0,equal(a,b,0)^(op==9));
    if((op==10||op==11)&&a.tag==BOOL&&b.tag==BOOL)return value(BOOL,0,op==10?(a.number&&b.number):(a.number||b.number));
    if(op==0&&a.tag==STRING&&b.tag==STRING)return value(STRING,address(concat(pointer(a.ref),pointer(b.ref))),0);
    operator_failure(f,a,b,symbols[op]);return empty;
}
static ArrayStorage *array_storage(u32 n) { ArrayStorage*s=allocate(sizeof(ArrayStorage)+n*sizeof(Value),VALUES);s->references=1;s->capacity=(((Block*)s-1)->size-sizeof(ArrayStorage))/sizeof(Value);s->used=n;return s; }
static void attach_storage(Array*a,ArrayStorage*s) { a->storage=s;a->capacity=s->capacity;a->values=s->values; }
static Array *array(u32 n) { Array*a=allocate(sizeof(Array),ARRAYS);a->count=n;attach_storage(a,array_storage(n));return a; }
static void writable_array(Array*a,u32 count) { if(a->storage->references==1&&count<=a->capacity)return;u32 capacity=count>a->count?count:a->count;if(capacity<a->capacity)capacity=a->capacity;ArrayStorage*old=a->storage,*next=array_storage(capacity);memcpy(next->values,a->values,a->count*sizeof(Value));next->used=a->count;if(!--old->references)release(old);attach_storage(a,next); }
static Array *append_array(Array*a,Value v) { Array*b=allocate(sizeof(Array),ARRAYS);b->count=a->count+1;if(a->count==a->storage->used&&b->count<=a->capacity){a->storage->references++;attach_storage(b,a->storage);}else{attach_storage(b,array_storage(b->count));memcpy(b->values,a->values,a->count*sizeof(Value));}b->values[a->count]=v;b->storage->used=b->count;return b; }
u32 aqua_array(u32 n) { return address(array(n)); }
u32 aqua_array_values(u32 p) { Array*a=pointer(p);writable_array(a,a->count);return address(a->values); }
u32 aqua_array_count(u32 p) { return ((Array*)pointer(p))->count; }
u32 aqua_array_resize(u32 p,u32 n) { Array*a=pointer(p);writable_array(a,n);if(n>a->count)memset(a->values+a->count,0,(n-a->count)*sizeof(Value));a->count=n;a->storage->used=n;return 0; }
u32 aqua_array_expose(u32 p) { Array*a=pointer(p);if(a->exposed!=2)a->exposed=1;return 0; }
/* Host-visible work lists: arrays and hashes changed since the host took them, and exposed ones the guest dropped
   (weak mirrors). Hosts read this block (aqua_pending) after a run slice or before a service call and call the take_*
   exports only for a non-empty list, instead of probing each list through a call. */
static Pending pending;
u32 aqua_pending(void) { return address(&pending); }
/* Members of host modules (NATIVE) resolved through host service 2, cached per (module, name) while the host's
   environment era is unchanged. Hosts write a nonzero era into the pending block whenever control returns to the
   guest after host bindings may have changed; era 0 disables the cache. Service 2 returns 0 for a member the guest may
   cache and 2 for a live property it must ask for on every read. Only values the guest does not own are cached (host
   functions and scalars), and every collection clears the cache so a native id the guest no longer references can be
   freed and reused. */
typedef struct { u32 native, name, era, pad; Value value; } MemberEntry;
static MemberEntry member_cache[256];
static MemberEntry *member_entry(u32 native,u32 name) { return &member_cache[(native*2654435761u^name>>2)&255]; }
static int member_cacheable(u32 tag) { return tag==INT||tag==FLOAT||tag==DOUBLE||tag==BOOL||tag==NULL_VALUE||tag==NATIVE||tag==BUILTIN; }
u32 aqua_take_dirty_array(void) { Array*a=pending.dirty_arrays;if(!a)return 0;pending.dirty_arrays=a->dirty_next;a->dirty_next=0;a->dirty=0;return address(a); }
/* Host reads: a borrowed view of an array's values (no copy-on-write), and whether an exposed array or hash
   changed in the guest since the host last took it from the dirty lists. Hosts refresh a mirror only when stale. */
/* Weak host mirrors. A host that opts in does not pin mirrored arrays and hashes. When the guest drops an exposed
   one, the collector keeps its block (exposed=2, children untraced) on a released list until the host takes it and
   forgets its mirror; the next collection frees it. Hosts take released objects after every run slice, service call
   and collection, before they could address a mirror's guest object again. */
static u32 weak_mirrors;
u32 aqua_weak_mirrors(void) { weak_mirrors=1;return 1; }
u32 aqua_take_released(void) {
    if(pending.released_arrays){Array*a=pending.released_arrays;pending.released_arrays=a->dirty_next;a->dirty_next=0;a->exposed=0;return address(a);}
    if(pending.released_hashes){Hash*h=pending.released_hashes;pending.released_hashes=h->dirty_next;h->dirty_next=0;h->exposed=0;return address(h);}
    return 0;
}
static int keep_released(Block*b) {
    if(!weak_mirrors)return 0;
    if(b->kind==ARRAYS){Array*a=(Array*)(b+1);if(a->exposed==1){a->exposed=2;a->dirty_next=pending.released_arrays;pending.released_arrays=a;}return a->exposed==2;}
    if(b->kind==HASHES){Hash*h=(Hash*)(b+1);if(h->exposed==1){h->exposed=2;h->dirty_next=pending.released_hashes;pending.released_hashes=h;}return h->exposed==2;}
    return 0;
}
u32 aqua_array_items(u32 p) { return address(((Array*)pointer(p))->values); }
u32 aqua_stale(u32 tag,u32 p) { return tag==ARRAY?((Array*)pointer(p))->dirty:tag==HASH?((Hash*)pointer(p))->dirty:1; }
u32 aqua_hash(u32 n) { Hash*h=allocate(sizeof(Hash),HASHES);h->count=n;h->pairs=allocate(n*sizeof(Pair),RAW);h->capacity=((Block*)h->pairs-1)->size/sizeof(Pair);return address(h); }
u32 aqua_hash_pairs(u32 p) { return address(((Hash*)pointer(p))->pairs); }
u32 aqua_hash_count(u32 p) { return ((Hash*)pointer(p))->count; }
u32 aqua_hash_resize(u32 p,u32 n) { Hash*h=pointer(p);if(n<h->indexed){release(h->index);h->index=0;h->indexed=0;}if(n>h->capacity){Pair*next=allocate(n*sizeof(Pair),RAW);memcpy(next,h->pairs,h->count*sizeof(Pair));release(h->pairs);h->pairs=next;h->capacity=((Block*)next-1)->size/sizeof(Pair);}if(n>h->count)memset(h->pairs+h->count,0,(n-h->count)*sizeof(Pair));h->count=n;return 0; }
u32 aqua_hash_write_support(void) { return 1; }
u32 aqua_hash_expose(u32 p) { Hash*h=pointer(p);if(h->exposed!=2)h->exposed=1;return 0; }
u32 aqua_take_dirty_hash(void) { Hash*h=pending.dirty_hashes;if(!h)return 0;pending.dirty_hashes=h->dirty_next;h->dirty_next=0;h->dirty=0;return address(h); }
/* A typed array keeps its element type: a generic write of another type is a language error. */
static int element_fits(Frame*f,Array*a,Value v) {
    static const char *const names[]={"","int","float","double","bool"};
    if(!a->element||v.tag==a->element)return 1;
    Text*t=concat(ascii("型別不符 / Type mismatch: the array holds "),ascii(names[a->element]));
    fail(f,concat(concat(t,ascii(", got ")),ascii(type_name(v.tag))));return 0;
}
#include "arrays.h"
#include "hashes.h"
#include "texts.h"
int rt_nativeStruct(int shape);
#include "threads.h"
static Frame *frame(Execution*e,Frame*parent,Program*p,u32 id,Scope*s,u32 context) {
    if(id>=p->count)__builtin_trap();Function*fn=p->functions+id;
    Frame*f=allocate(sizeof(Frame)+(fn->locals+fn->capacity)*sizeof(Value),FRAMES);f->parent=parent;f->execution=e;f->program=p;f->function=fn;f->scope=s;f->context=context;f->capacity=fn->locals+fn->capacity;f->sp=fn->locals;for(u32 i=0;i<fn->locals;i++)f->values[i].tag=UNDECLARED;u32 cached=fn->wrap_return>>1;if(cached&&!(fn->flags&PROMOTED_SCOPE))f->binding_cache=allocate(cached*8,RAW);return f;
}
static void retain_scope(Scope*s) { for(;s&&!s->captured;s=s->outer)s->captured=1; }
static void dispose_scope(Scope*s) { if(s&&!s->captured){release(s->bindings);release(s->index);release(s);} }
static Value constant(Frame*f,u32 n) { return f->function->constants[n]; }
static Text *name(Frame*f,u32 n) { return pointer(constant(f,n).ref); }
static Binding *cached_binding(Frame*f,u32 n) { u32*c=f->binding_cache?f->binding_cache+n*2:0;if(c&&c[0]==scope_epoch&&c[1])return pointer(c[1]);Binding*b=find(f->scope,name(f,n));if(c){c[0]=scope_epoch;c[1]=address(b);}return b; }
/* Address of a lexical binding's value, or 0 when the scope chain has none (no failure). */
int rt_lookup(Frame*f,int n) { Binding*b=cached_binding(f,(u32)n);return b?(int)address(&b->value):0; }
int rt_binding(Frame*f,int n) { Binding*b=cached_binding(f,(u32)n);if(!b){named_failure(f,"Identifier not found: ",name(f,(u32)n));return 0;}return (int)address(&b->value); }
static void release_frame(Frame*f) { if(!(f->function->flags&PROMOTED_SCOPE))release(f->binding_cache);release(f); }
/* A promoted call resolves free names from the closure scope itself, so lookups can be shared by all calls. */
static u32 *closure_cache(Closure*c,Function*fn) {
    u32 cached=fn->wrap_return>>1;if(!cached)return 0;
    if(!c->cache){c->cache=allocate(cached*8,RAW);c->cache_era=epoch_era;}
    else if(c->cache_era!=epoch_era){memset(c->cache,0,cached*8);c->cache_era=epoch_era;}
    return c->cache;
}
static void complete_frame(Execution*e,Frame*f) {
    Value result=f->sp>f->function->locals?pop(f):empty;Frame*parent=f->parent;
    if(!(f->function->flags&PROMOTED_SCOPE))dispose_scope(f->scope);release_frame(f);e->current=parent;push(parent,result);
}
/* Run a callee frame just pushed by rt_call on the native stack. Every frame stays materialized in Wasm
   memory, so a callee that suspends, yields or exceeds the nesting bound simply returns its continuation
   and the scheduler resumes the innermost frame; nothing is speculated or replayed. */
#define NATIVE_CALL_DEPTH 128
static u32 native_depth;
/* Resumes a deoptimized native function natively when the scheduler runs its marked frame again: at its entry or at
   the loop head where its budget ran out. The resume adapter returns 1 with the result, 4 after deoptimizing again
   (a new frame chain replaces this one), 5 after raising an error, 0 while the budget is exhausted or a collection
   is pending (the generic body runs and yields at its checkpoint) and 6 when a binding changed (generic from now on).
   Returns 1 when the frame was handled. */
static int native_resume(Execution*e,Frame*f) {
    Function*fn=f->function;
    if(!(fn->flags&NATIVE_TIER)){f->native_site=0;return 0;}
    Value out=empty;e->numeric_closure=f->native_closure;e->numeric_innermost=0;
    int status=((int(*)(Frame*,Value*,Execution*))(uintptr_t)(fn->numeric_table+1))(f,&out,e);
    if(status==0)return 0;
    if(status==6){f->native_site=0;return 0;}
    if(status==5)return 1;
    Frame*parent=f->parent;release_frame(f);
    if(status==4){Frame*outer=pointer(out.ref);outer->parent=parent;e->current=e->numeric_innermost;return 1;}
    e->current=parent;if(!parent){e->result=out;e->status=4;}else push(parent,out);return 1;
}
/* A frame marked to resume natively (native_site) stays marked only while it is still at the marked block: when native
   code declines (no budget left, a collection pending) the generic body runs it, and if that body suspends it anywhere
   else, resuming at the old site would repeat work, so the mark is dropped. */
static void suspended(Frame*f,int next) { if(f->native_site&&(u32)next!=f->pc)f->native_site=0;f->pc=(u32)next; }
static int run_callee(Frame*f) {
    // Deoptimized specializations can push a chain of frames, so run until control returns to f.
    Execution*e=f->execution;
    while(e->current!=f){
        Frame*callee=e->current;
        if(e->status||native_depth>=NATIVE_CALL_DEPTH)return 1;
        if(callee->native_site&&native_resume(e,callee)){if(e->status)return 1;continue;}
        native_depth++;int next=((int(*)(Frame*))(uintptr_t)callee->function->table)(callee);native_depth--;
        if(e->status==3)return 1;
        if(next>=0){suspended(callee,next);return 1;}
        complete_frame(e,callee);if(e->status)return 1;
    }
    return 0;
}
u32 aqua_execute(u32 p,u32 id,u32 s,u32 context) { Execution*e=allocate(sizeof(Execution),EXECUTIONS);e->next=executions;executions=e;e->poll=gc_armed;((Scope*)pointer(s))->captured=1;e->current=frame(e,0,pointer(p),id,pointer(s),context);return address(e); }
u32 aqua_invoke(u32 closure,u32 arguments,u32 count) {
    Closure*c=pointer(closure);Function*fn=c->program->functions+c->function;Value*a=pointer(arguments);
    if(fn->flags&PROMOTED_SCOPE){Execution*e=pointer(aqua_execute(address(c->program),c->function,address(c->scope),c->context));
        if(count!=fn->arity){failure(e->current,"Incorrect callback argument count.");return address(e);}
        e->current->binding_cache=closure_cache(c,fn);for(u32 i=0;i<count;i++)e->current->values[i]=a[i];return address(e);}
    Execution*e=pointer(aqua_execute(address(c->program),c->function,address(scope(c->scope)),c->context));
    e->current->scope->captured=0;
    if(count!=fn->arity){failure(e->current,"Incorrect callback argument count.");return address(e);}
    for(u32 i=0;i<count;i++)declare(e->current->scope,fn->parameters[i],a[i]);return address(e);
}
/* A Thread worker runs without the main program's host: it reaches the builtins every host defines, Thread.Sleep
   and Thread.IsCancellationRequested; anything else is a language error. Every host's worker therefore implements
   only those two calls. */
static int worker;
static int host_service(int operation, int context, int subject, int name, int arguments, int count, int output) {
    if(!worker||operation==5||(operation==4&&(subject==106||subject==107)))return host_import(operation,context,subject,name,arguments,count,output);
    Value*out=pointer((u32)output);
    if(operation==0){
        static const uint16_t *const builtins[][2]={{u"len",u"長度"},{u"last",u"最後一個"},{u"rest",u"其餘"},{u"push",u"加入"},{u"floodFill",u"區域填色"}};
        for(u32 i=0;i<5;i++)if(text_is(pointer((u32)name),builtins[i][0])||text_is(pointer((u32)name),builtins[i][1])){*out=value(BUILTIN,i+1,0);return 0;}
        static const uint16_t *const conversions[][2]={{u"int",u"整數"},{u"float",u"浮點數"},{u"double",u"雙精度"}};
        for(u32 i=0;i<3;i++)if(text_is(pointer((u32)name),conversions[i][0])||text_is(pointer((u32)name),conversions[i][1])){*out=value(BUILTIN,CONVERT_FIRST_ID+i,0);return 0;}
        return 1; /* Identifier not found */
    }
    *out=value(ERROR,address(ascii("Worker cannot access host handles or start/wait for other threads.")),0);
    return 0;
}
u32 aqua_thread_enter(u32 closure,u32 arguments,u32 count) {
    // The snapshot carries the parent's heap limit; this instance owns only the memory it reserved.
    executions=0;pins=0;memset(builtin_scopes,0,sizeof(builtin_scopes));memset(portable_modules,0,sizeof(portable_modules));builtin_context_count=0;heap_limit=(u32)__builtin_wasm_memory_size(0)*65536;
    worker=1;
    return aqua_invoke(closure,arguments,count);
}
u32 aqua_thread_snapshot_size(void) { return heap_end; }
u32 aqua_thread_reserve(u32 n) {
    u32 available=(u32)__builtin_wasm_memory_size(0)*65536;
    if(n>available&&__builtin_wasm_memory_grow(0,(n-available+65535)/65536)==(size_t)-1)__builtin_trap();return 0;
}
u32 aqua_result(u32 e) { return address(&((Execution*)pointer(e))->result); }
u32 aqua_status(u32 e) { return ((Execution*)pointer(e))->status; }
u32 aqua_resume(u32 e,u32 v) { Execution*x=pointer(e);x->status=0;x->current->pending_count=0;push(x->current,*(Value*)pointer(v));return 0; }
u32 aqua_release_execution(u32 ptr) { Execution*e=pointer(ptr);Execution**p=&executions;while(*p&&*p!=e)p=&(*p)->next;if(*p)*p=e->next;for(Frame*f=e->current;f;){Frame*next=f->parent;release_frame(f);f=next;}release(e);return 0; }
u32 aqua_pin(u32 v) { Pin*p=allocate(sizeof(Pin),PINS);p->value=*(Value*)pointer(v);p->prev=0;p->next=pins;if(pins)pins->prev=p;pins=p;return address(&p->value); }
/* O(1): the handle is the pin's value; anything that is not a live pin is ignored. */
u32 aqua_unpin(u32 v) {
    if(v<heap_start+sizeof(Block)+__builtin_offsetof(Pin,value)||v>=heap_end)return 0;
    Pin*x=(Pin*)((char*)pointer(v)-__builtin_offsetof(Pin,value));Block*b=(Block*)x-1;if(b->kind!=PINS)return 0;
    if(x->prev)x->prev->next=x->next;else pins=x->next;if(x->next)x->next->prev=x->prev;release(x);return 0;
}
u32 aqua_run(u32 ptr,u32 budget) {
    Execution*e=pointer(ptr);if(e->status==2||e->status==3)return e->status;e->budget=budget;e->status=0;
    while(e->current){Frame*f=e->current;
        if(f->native_site&&native_resume(e,f)){if(e->status)return e->status;continue;}
        int next=((int(*)(Frame*))(uintptr_t)f->function->table)(f);
        if(e->status==3)return 3;
        if(next<0){Value result=f->sp>f->function->locals?pop(f):empty;Frame*parent=f->parent;if(!(f->function->flags&PROMOTED_SCOPE))dispose_scope(f->scope);release_frame(f);e->current=parent;if(!parent){e->result=result;e->status=4;return 4;}push(parent,result);}
        else suspended(f,next);
        if(e->status==2)return 2;if(e->status==1)return 1;
    }e->status=4;return 4;
}
int rt_pc(Frame*f,int unused) { return (int)f->pc; }
int rt_failed(Frame*f,int unused) { return f->execution->status==3; }
/* Fuel: a checkpoint consumes its static weight (see WasmFuel), saturating at zero, and yields only once the
   budget is exhausted, so every quantum makes progress even when a weight exceeds the quantum. */
int rt_checkpoint(Frame*f,int weight) { if(collection_due())aqua_collect();u32 w=weight>1?(u32)weight:1;Execution*e=f->execution;if(!e->budget){e->status=1;return 1;}e->budget=e->budget>w?e->budget-w:0;return 0; }
int rt_constant(Frame*f,int n) { push(f,constant(f,n));return 0; }
int rt_void(Frame*f,int n) { push(f,empty);return 0; }
int rt_null(Frame*f,int n) { push(f,value(NULL_VALUE,0,0));return 0; }
int rt_pop(Frame*f,int n) { pop(f);return 0; }
int rt_duplicate(Frame*f,int n) { push(f,peek(f,1));return 0; }
/* Built-in portable modules: each has an English and a Traditional Chinese global name. */
static Scope *portable_module(Text*t,const uint16_t**alias);
/* The portable module (Thread, Array, Hash, String or a Chinese name) a host import resolves to, or 0. */
u32 aqua_portable_module(u32 name) { const uint16_t*alias=0;return address(portable_module(pointer(name),&alias)); }
static Scope *portable_module(Text*t,const uint16_t**alias) {
    const uint16_t *const *names[]={thread_module_names,array_module_names,hash_module_names,text_module_names};
    for(u32 i=0;i<4;i++)for(u32 j=0;j<2;j++)if(text_is(t,names[i][j])){
        *alias=names[i][1-j];
        if(!portable_modules[i])portable_modules[i]=i==0?thread_library():i==1?array_library():i==2?hash_library():text_library();
        return portable_modules[i];
    }
    return 0;
}
int rt_load(Frame*f,int n) { Binding*b=cached_binding(f,n);if(!b&&f->context<65536&&builtin_scopes[f->context])b=find_owned(builtin_scopes[f->context],name(f,n));if(b)push(f,b->value);else {Value v=empty;Text*t=name(f,n);int status;const uint16_t*alias=0;
    Scope*module=portable_module(t,&alias);
    if(module){v=value(MODULE,address(module),0);status=0;}else status=host_service(0,f->context,0,address(t),0,0,address(&v));
    if(status)named_failure(f,"Identifier not found: ",t);else {if(f->context<65536){if(!builtin_scopes[f->context]){builtin_scopes[f->context]=scope(0);builtin_contexts[builtin_context_count++]=f->context;}declare(builtin_scopes[f->context],t,v);
        if(alias)declare(builtin_scopes[f->context],utf16(alias),v);
    }push(f,v);}}return 0; }
int rt_declare(Frame*f,int n) { declare(f->scope,name(f,n),pop(f));push(f,empty);return 0; }
int rt_assign(Frame*f,int n) { Value v=pop(f);pop(f);Binding*b=cached_binding(f,n);if(!b)named_failure(f,"Identifier not found: ",name(f,n));else b->value=v;push(f,empty);return 0; }
int rt_compoundAssign(Frame*f,int n) { Value c=constant(f,n),r=pop(f);pop(f);Binding*b=cached_binding(f,n);if(!b)named_failure(f,"Identifier not found: ",pointer(c.ref));else b->value=binary(f,(int)c.number,b->value,r,1);push(f,empty);return 0; }
#define BINARY(NAME,OP) int rt_##NAME(Frame*f,int n){Value r=pop(f),l=pop(f);push(f,binary(f,OP,l,r,0));return 0;}
BINARY(add,0) BINARY(subtract,1) BINARY(multiply,2) BINARY(divide,3) BINARY(less,4) BINARY(greater,5) BINARY(lessEqual,6) BINARY(greaterEqual,7) BINARY(equal,8) BINARY(notEqual,9) BINARY(and,10) BINARY(or,11) BINARY(modulo,12)
int rt_negate(Frame*f,int n) { Value v=pop(f);if(numeric(v))push(f,v.tag==INT?integer(-(int64_t)v.number):number(-v.number,v.tag));else named_failure(f,"Unknown operator: -",ascii(type_name(v.tag)));return 0; }
int rt_not(Frame*f,int n) { Value v=pop(f);push(f,value(BOOL,0,v.tag==BOOL?!v.number:v.tag==NULL_VALUE));return 0; }
static int increment(Frame*f,int n,int prefix) { Binding*b=cached_binding(f,n);if(!b){named_failure(f,"Identifier not found: ",name(f,n));return 0;}Value old=b->value;if(!numeric(old)){failure(f,"Increment requires a number.");return 0;}b->value=successor(old);push(f,prefix?b->value:old);return 0; }
/* Promoted locals are frame slots rather than named bindings; these helpers receive the slot address. */
int rt_incrementAt(Frame*f,int prefix,Value*b) { Value old=*b;if(!numeric(old)){failure(f,"Increment requires a number.");return 0;}*b=successor(old);push(f,prefix?*b:old);return 0; }
int rt_compoundAt(Frame*f,int n,Value*b) { Value c=constant(f,n),r=pop(f);pop(f);*b=binary(f,(int)c.number,*b,r,1);push(f,empty);return 0; }
int rt_incrementPrefix(Frame*f,int n){return increment(f,n,1);}int rt_incrementPostfix(Frame*f,int n){return increment(f,n,0);}
int rt_truth(Frame*f,int n) { Value v=pop(f);return v.tag==BOOL?v.number!=0:v.tag!=NULL_VALUE; }
int rt_isBreak(Frame*f,int n) { return peek(f,1).tag==BREAK_VALUE; }
int rt_closure(Frame*f,int n) { Value c=constant(f,n);Closure*x=allocate(sizeof(Closure),CLOSURES);x->program=f->program;x->function=(u32)c.number;x->scope=f->scope;x->context=f->context;retain_scope(f->scope);push(f,value(CLOSURE,address(x),0));return 0; }
typedef struct { u32 y,left,right; } FillSpan;
/* Portable four-connected scanline fill. Paint discovered spans before queuing
   them, so holes and branching boundaries cannot enqueue the same run twice. */
/* Native writes into a typed array whose storage is shared or exposed: copy-on-write and host dirty tracking. */
int rt_nativeWritable(int array) { Array*a=pointer((u32)array);writable_array(a,a->count);array_changed(a);return 0; }
static Value flood_fill(Frame*f,Value*args) {
    if(args[0].tag!=ARRAY||args[1].tag!=INT||args[2].tag!=INT||args[3].tag!=INT||args[4].tag!=INT||!numeric(args[5])){failure(f,"floodFill requires a numeric array, integer dimensions/coordinates and a numeric replacement.");return empty;}
    Array*a=pointer(args[0].ref);if(!element_fits(f,a,args[5]))return empty;double wd=args[1].number,hd=args[2].number,xd=args[3].number,yd=args[4].number;
    if(wd<1||hd<1||wd>8192||hd>8192||wd*hd>16u*1024*1024||wd*hd!=a->count||xd<0||yd<0||xd>=wd||yd>=hd){failure(f,"Invalid floodFill dimensions or seed.");return empty;}
    for(u32 i=0;i<a->count;i++)if(!numeric(a->values[i])){failure(f,"floodFill requires a numeric array.");return empty;}
    u32 width=(u32)wd,height=(u32)hd,x=(u32)xd,y=(u32)yd;Value original=a->values[y*width+x],replacement=args[5];
    if(equal(original,replacement,0)||!equal(original,original,0))return number(0,INT);
    writable_array(a,a->count);u32 capacity=64,count=0,changed=0;FillSpan*stack=allocate(capacity*sizeof(FillSpan),RAW);
    u32 left=x,right=x;while(left&&equal(a->values[y*width+left-1],original,0))left--;while(right+1<width&&equal(a->values[y*width+right+1],original,0))right++;
    for(u32 i=left;i<=right;i++){a->values[y*width+i]=replacement;changed++;}stack[count++]=(FillSpan){y,left,right};
    while(count){FillSpan span=stack[--count];for(u32 side=0;side<2;side++){
        if((side==0&&span.y==0)||(side==1&&span.y+1==height))continue;u32 row=side?span.y+1:span.y-1;
        for(u32 column=span.left;column<=span.right;){
            if(!equal(a->values[row*width+column],original,0)){column++;continue;}
            left=right=column;while(left&&equal(a->values[row*width+left-1],original,0))left--;while(right+1<width&&equal(a->values[row*width+right+1],original,0))right++;
            if(count==capacity){capacity*=2;FillSpan*next=allocate(capacity*sizeof(FillSpan),RAW);memcpy(next,stack,count*sizeof(FillSpan));release(stack);stack=next;}
            for(u32 i=left;i<=right;i++){a->values[row*width+i]=replacement;changed++;}stack[count++]=(FillSpan){row,left,right};column=right+1;
        }
    }}release(stack);if(a->exposed&&!a->dirty){a->dirty=1;a->dirty_next=pending.dirty_arrays;pending.dirty_arrays=a;}return number(changed,INT);
}
static Value struct_new(Frame*f,Shape*s,Value*args,u32 n,Text*names);
int rt_call(Frame*f,int n) {
    if(n<0||(u32)n>=f->sp){failure(f,"Invalid compiled call.");return 1;}u32 base=f->sp-n-1;Value callee=f->values[base];Value*args=f->values+base+1;
    if(callee.tag==BUILTIN&&callee.ref>=THREAD_FIRST_ID&&callee.ref<THREAD_FIRST_ID+THREAD_COUNT){
        if(!thread_arguments(f,callee.ref,args,n))return 1;
        Value out=empty;int pending=host_service(4,f->context,callee.ref,0,address(args),n,address(&out));f->sp=base;
        if(pending==1){f->pending_base=base;f->pending_count=n+1;f->execution->status=2;}else push(f,out);
    }
    else if(callee.tag==BUILTIN&&callee.ref>=ARRAY_FIRST_ID&&callee.ref<TEXT_FIRST_ID+TEXT_COUNT){Value out=callee.ref<HASH_FIRST_ID?array_call(f,callee.ref,args,n):callee.ref<TEXT_FIRST_ID?hash_call(f,callee.ref,args,n):text_call(f,callee.ref,args,n);f->sp=base;push(f,out);}
    else if(callee.tag==BUILTIN&&callee.ref>=KERNEL_FIRST_ID&&callee.ref<KERNEL_FIRST_ID+KERNEL_COUNT){Value out;u32 prelude=kernel_call(f,callee.ref,args,(u32)n,&out);
        if(prelude&&!f->execution->status){Closure*c=prelude_closure(f,prelude);if(!c)return 1;f->values[base]=value(CLOSURE,address(c),0);return rt_call(f,n);}
        f->sp=base;push(f,out);}
    else if(callee.tag==BUILTIN&&callee.ref>=CONVERT_FIRST_ID&&callee.ref<CONVERT_FIRST_ID+3){Value out=empty;
        if(n!=1)failure(f,"Incorrect builtin argument count.");
        else if(!numeric(args[0]))named_failure(f,"Conversion requires a number / 轉換需要數字: ",ascii(type_name(args[0].tag)));
        else out=number(args[0].number,INT+callee.ref-CONVERT_FIRST_ID);
        f->sp=base;push(f,out);}
    else if(callee.tag==BUILTIN){Value out=empty;if(callee.ref<1||callee.ref>5||n!=(callee.ref==5?6:callee.ref==4?2:1)){failure(f,"Incorrect builtin argument count.");return 1;}Value a=args[0];
        if(callee.ref==5)out=flood_fill(f,args);
        else if(callee.ref==1){if(a.tag==STRING)out=number(((Text*)pointer(a.ref))->length,INT);else if(a.tag==ARRAY)out=number(((Array*)pointer(a.ref))->count,INT);else if(a.tag==HASH)out=number(((Hash*)pointer(a.ref))->count,INT);else failure(f,"Length requires string, array or hash.");}
        else if(a.tag!=ARRAY)failure(f,"Builtin requires an array.");else{Array*x=pointer(a.ref);if(callee.ref==2)out=x->count?x->values[x->count-1]:value(NULL_VALUE,0,0);
            else if(callee.ref==3){if(!x->count)out=value(NULL_VALUE,0,0);else{Array*y=array(x->count-1);memcpy(y->values,x->values+1,y->count*sizeof(Value));out=value(ARRAY,address(y),0);}}
            else{Array*y=append_array(x,args[1]);out=value(ARRAY,address(y),0);}}
        f->sp=base;push(f,out);}
    else if(callee.tag==STRUCT_TYPE){Value out=struct_new(f,pointer(callee.ref),args,(u32)n,0);f->sp=base;if(!f->execution->status)push(f,out);}
    else if(callee.tag==CLOSURE){Closure*c=pointer(callee.ref);Function*fn=c->program->functions+c->function;if(fn->arity!=(u32)n){Text*t=concat(ascii("Function expects "),decimal(fn->arity));t=concat(t,ascii(" arguments, got "));t=concat(t,decimal(n));fail(f,concat(t,ascii(".")));return 1;}
        // Closed scalar functions have no heap/capability/captured-write effects.
        // A failed fast attempt can therefore restart on the resumable path.
        // Speculation that keeps failing its argument guards (e.g. always non-numeric arguments) is skipped.
        if(fn->numeric_table&&f->execution->numeric_depth_disabled!=fn&&!(NUMERIC_MISSES(fn)>=64&&NUMERIC_HITS(fn)*4<NUMERIC_MISSES(fn))){Binding*self=fn->recursive_name?find(c->scope,fn->recursive_name):0;
            if(!fn->recursive_name||(self&&self->value.tag==CLOSURE&&self->value.ref==callee.ref)){
                u32 saved_budget=f->execution->budget;Value out=empty;f->execution->numeric_closure=c;f->execution->numeric_innermost=0;
                int status=((int(*)(Value*,Value*,Execution*))(uintptr_t)fn->numeric_table)(args,&out,f->execution);
                if(status==1||status==4){if(NUMERIC_HITS(fn)<255)fn->flags+=1u<<16;}else if(status==0&&NUMERIC_MISSES(fn)<255)fn->flags+=1u<<24;
                if(status==1){numeric_fast_calls++;f->sp=base;push(f,out);return f->execution->status!=0;}
                if(status==4){numeric_fast_calls++;numeric_deopts++;Frame*outer=pointer(out.ref);outer->parent=f;f->sp=base;f->execution->current=f->execution->numeric_innermost;return run_callee(f);}
                if(status==5){numeric_fast_calls++;f->sp=base;return 1;} /* A native function raised a language error (rt_nativeFailure). */
                numeric_fallback_calls++;if(status==3){numeric_depth_fallbacks++;f->execution->numeric_depth_disabled=fn;}
                // Discarded speculation must not cause an observable yield before
                // the generic body reconstructs its actual execution state.
                f->execution->budget=saved_budget;
            }
        }
        if(fn->flags&PROMOTED_SCOPE){Frame*callee=frame(f->execution,f,c->program,c->function,c->scope,c->context);callee->binding_cache=closure_cache(c,fn);for(int i=0;i<n;i++)callee->values[i]=args[i];f->sp=base;f->execution->current=callee;return run_callee(f);}
        Scope*s=scope(c->scope);for(int i=0;i<n;i++)declare(s,fn->parameters[i],args[i]);f->sp=base;f->execution->current=frame(f->execution,f,c->program,c->function,s,c->context);return run_callee(f);}
    else if(callee.tag==NATIVE){Value out=empty;int pending=host_service(1,f->context,callee.ref,0,address(args),n,address(&out));f->sp=base;if(pending==1){f->pending_base=base;f->pending_count=n+1;f->execution->status=2;}else push(f,out);}
    else named_failure(f,"Not a function: ",ascii(type_name(callee.tag)));return f->execution->status!=0;
}
int rt_returned(Frame*f,int n){f->returned=1;return 0;}
/* Deoptimization of a proven integer specialization: a generic frame of the closure being specialized,
   resuming at block pc with depth operands; the specialization writes its variables and operands. */
int rt_numericFrame(Execution*e,int pc,int depth) {
    Closure*c=e->numeric_closure;Function*fn=c->program->functions+c->function;
    Frame*f=frame(e,0,c->program,c->function,c->scope,c->context);f->binding_cache=closure_cache(c,fn);
    f->pc=(u32)pc;f->sp=fn->locals+(u32)depth;if(!e->numeric_innermost)e->numeric_innermost=f;return (int)address(f);
}
/* A hot loop asks the runtime JIT (desktop hosts) for native code: the host answers the table index of the next variant
   to try, or -1 when none remains or it has no JIT. */
int rt_jitRequest(Frame*f,int loop) { int index=host_service(5,f->context,loop,0,0,0,0);return index>0?index:-1; }
/* Deoptimization of a native (fully typed) function: like rt_numericFrame, for the given closure, since native
   functions call each other directly. The caller writes the frame's variables and operands. */
int rt_nativeFrame(Execution*e,int closure,int pc,int depth) {
    Closure*c=pointer((u32)closure);Function*fn=c->program->functions+c->function;
    Frame*f=frame(e,0,c->program,c->function,c->scope,c->context);f->binding_cache=closure_cache(c,fn);f->native_closure=c;
    f->pc=(u32)pc;f->sp=fn->locals+(u32)depth;if(!e->numeric_innermost)e->numeric_innermost=f;return (int)address(f);
}
/* A language error raised inside a native function, with the generic runtime's message; the native code then
   returns status 5 through every native activation. */
int rt_nativeFailure(Execution*e,int code) {
    static const char *const messages[]={"Integer division by zero / 整數不能除以零。","Integer remainder by zero / 整數不能對零取餘數。",
        "Array index is out of bounds / 陣列索引超出範圍。","Array assignment index is out of bounds.",
        "Cannot access member of NULL","Cannot assign a member of NULL"};
    e->result=value(ERROR,address(ascii(messages[code])),0);e->status=3;return 5;
}
/* An array literal built by native code (an array of any values): it writes every element before anything can collect. */
int rt_nativeArray(int n){return (int)address(array((u32)n));}
/* ---- Structs (結構 / struct) ---- */
/* Whether v fits annotation code (widening numbers in place): 0 any value; a scalar tag; 16 | element a typed array
   (locking an untyped one whose elements all fit); 16 any array; 32 | id << 6 a struct of that type or null. */
static int fits(Value*v,u32 code) {
    if(!code)return 1;
    if(code&32u)return v->tag==NULL_VALUE||(v->tag==STRUCT&&((Struct*)pointer(v->ref))->shape->id==code>>6);
    u32 want=code&15u;
    if(code&16u){if(v->tag!=ARRAY)return 0;Array*a=pointer(v->ref);if(!want||a->element==want)return 1;
        u32 i=0;if(!a->element)while(i<a->count&&a->values[i].tag==want)i++;if(!a->element&&i==a->count){a->element=want;return 1;}return 0;}
    if(v->tag==want)return 1;
    if(numeric(*v)&&want>=FLOAT&&want<=DOUBLE&&v->tag<want){*v=number(v->number,want);return 1;}
    return 0;
}
/* The name of a value's type in messages: a struct's own name, else the tag's. */
static Text *value_type(Value v) { return v.tag==STRUCT?((Struct*)pointer(v.ref))->shape->name:ascii(type_name(v.tag)); }
static Text *code_name(u32 code,Text*type) {
    static const char *const names[]={"","int","float","double","bool","string"};
    if(code&32u)return type;if(code==16u)return ascii("array");
    Text*t=ascii(names[code&15u]);return code&16u?concat(t,ascii("[]")):t;
}
static void mismatch(Frame*f,Text*subject,Text*expected,Value v) {
    Text*t=concat(concat(ascii("型別不符 / Type mismatch: "),subject),ascii(" expects "));
    fail(f,concat(concat(concat(t,expected),ascii(", got ")),value_type(v)));
}
/* Pieces of a compiler descriptor string: fields separated by U+0001, parts of a field by U+0002. */
static Text *piece(Text*d,u32*at,uint16_t stop) {
    u32 start=*at,end=start;while(end<d->length&&d->data[end]!=1&&d->data[end]!=stop)end++;
    Text*t=pointer(aqua_text(end-start));memcpy(t->data,d->data+start,(end-start)*2);*at=end<d->length?end+1:end;return t;
}
static u32 decimal_value(Text*t) { u32 n=0;for(u32 i=0;i<t->length;i++)n=n*10+(u32)(t->data[i]-'0');return n; }
static void unknown_field(Frame*f,Shape*s,Text*field) { fail(f,concat(concat(concat(ascii("Unknown field of "),s->name),ascii(": ")),field)); }
static int field_index(Shape*s,Text*name) { for(u32 i=0;i<s->count;i++)if(text_equal(s->fields[i].name,name))return (int)i;return -1; }
static int field_fits(Frame*f,Shape*s,u32 i,Value*v) {
    if(fits(v,s->fields[i].code))return 1;
    mismatch(f,concat(concat(s->name,ascii(".")),s->fields[i].name),code_name(s->fields[i].code,s->fields[i].type),*v);return 0;
}
/* A struct declaration: descriptor "id\1name\1field\2code\2type\1..." (an untyped field has code 0). */
int rt_structType(Frame*f,int n) {
    Text*d=name(f,(u32)n);u32 at=0,count=0;for(u32 i=0;i<d->length;i++)if(d->data[i]==1)count++;count--;
    Shape*s=allocate(sizeof(Shape)+count*sizeof(ShapeField),SHAPES);s->id=decimal_value(piece(d,&at,2));s->name=piece(d,&at,2);s->count=count;
    for(u32 i=0;i<count;i++){s->fields[i].name=piece(d,&at,2);s->fields[i].code=decimal_value(piece(d,&at,2));s->fields[i].type=piece(d,&at,2);}
    push(f,value(STRUCT_TYPE,address(s),0));return 0;
}
/* A new struct of type s from n arguments: field[i] gets args[i], or args by name when names (a descriptor of field names)
   is given. Fields without an argument are null, which only untyped and struct-typed fields may hold. */
static Value struct_new(Frame*f,Shape*s,Value*args,u32 n,Text*names) {
    if(!names&&n>s->count){fail(f,concat(concat(ascii("Too many fields for "),s->name),ascii(".")));return empty;}
    Struct*x=allocate(sizeof(Struct)+s->count*sizeof(Value),STRUCTS);x->shape=s;x->count=s->count;
    for(u32 i=0;i<s->count;i++)x->fields[i]=value(NULL_VALUE,0,0);
    u32 at=0;unsigned char given[256]={0};
    for(u32 i=0;i<n;i++){int k=(int)i;
        if(names){Text*field=piece(names,&at,2);k=field_index(s,field);
            if(k<0){unknown_field(f,s,field);return empty;}
            if(k<256&&given[k]++){fail(f,concat(concat(concat(ascii("Field given twice for "),s->name),ascii(": ")),field));return empty;}}
        Value v=args[i];if(!field_fits(f,s,(u32)k,&v))return empty;x->fields[k]=v;}
    for(u32 i=0;i<s->count;i++)if(x->fields[i].tag==NULL_VALUE&&!fits(&x->fields[i],s->fields[i].code)){Value v=x->fields[i];field_fits(f,s,i,&v);return empty;}
    return value(STRUCT,address(x),0);
}
/* A construction with named arguments: 節點(左: a, 右: b); n names the constant listing the field names in order. */
int rt_callNamed(Frame*f,int n) {
    Text*names=name(f,(u32)n);u32 count=1;for(u32 i=0;i<names->length;i++)if(names->data[i]==1)count++;
    if(count>=f->sp){failure(f,"Invalid compiled call.");return 0;}
    u32 base=f->sp-count-1;Value callee=f->values[base];
    if(callee.tag!=STRUCT_TYPE){fail(f,concat(ascii("Named arguments need a struct type, got "),value_type(callee)));return 0;}
    Value out=struct_new(f,pointer(callee.ref),f->values+base+1,count,names);f->sp=base;if(!f->execution->status)push(f,out);return 0;
}
/* obj.field = value (the value stays on the stack, like an assignment). */
int rt_setMember(Frame*f,int n) {
    Value v=pop(f),m=pop(f);Text*t=name(f,(u32)n);
    if(m.tag!=STRUCT){fail(f,concat(ascii("Cannot assign a member of "),value_type(m)));return 0;}
    Struct*x=pointer(m.ref);int k=field_index(x->shape,t);
    if(k<0){unknown_field(f,x->shape,t);return 0;}
    if(!field_fits(f,x->shape,(u32)k,&v))return 0;x->fields[k]=v;push(f,v);return 0;
}
/* obj.field op= value: the field keeps its type like a compound assignment to a variable. */
int rt_compoundMember(Frame*f,int n) {
    Value c=constant(f,(u32)n),r=pop(f),m=pop(f);Text*t=pointer(c.ref);
    if(m.tag!=STRUCT){fail(f,concat(ascii("Cannot assign a member of "),value_type(m)));return 0;}
    Struct*x=pointer(m.ref);int k=field_index(x->shape,t);
    if(k<0){unknown_field(f,x->shape,t);return 0;}
    Value v=binary(f,(int)c.number,x->fields[k],r,1);if(f->execution->status||!field_fits(f,x->shape,(u32)k,&v))return 0;
    x->fields[k]=v;push(f,empty);return 0;
}
/* An annotation of a struct type on the top operand: descriptor "id\1subject\1type name". */
int rt_checkStruct(Frame*f,int n) {
    if(!f->sp)return 0;Text*d=name(f,(u32)n);u32 at=0;u32 id=decimal_value(piece(d,&at,2));Text*subject=piece(d,&at,2),*type=piece(d,&at,2);
    if(!fits(f->values+f->sp-1,32u|id<<6))mismatch(f,subject,type,f->values[f->sp-1]);return 0;
}
/* Native code builds a struct of a known type: every field is written before anything can collect. */
int rt_nativeStruct(int shape) {
    Shape*s=pointer((u32)shape);Struct*x=allocate(sizeof(Struct)+s->count*sizeof(Value),STRUCTS);x->shape=s;x->count=s->count;
    for(u32 i=0;i<s->count;i++)x->fields[i]=value(NULL_VALUE,0,0);return (int)address(x);
}
int rt_array(Frame*f,int n){Array*a=array(n);for(int i=n-1;i>=0;i--)a->values[i]=pop(f);push(f,value(ARRAY,address(a),0));return 0;}
/* An annotation check on the top operand: n = (subject constant << 5) | type code (element tag, +16 for arrays).
   Integers widen to float or double and floats to double; any other mismatch is a language error. */
int rt_checkType(Frame*f,int n){
    static const char *const names[]={"","int","float","double","bool","string"};
    if(!f->sp)return 0;Value*v=f->values+f->sp-1;u32 code=(u32)n&31u,want=code&15u;
    if(code&16u){if(v->tag==ARRAY){Array*a=pointer(v->ref);if(!want||a->element==want)return 0; /* 陣列 / array: any array */
        u32 i=0;if(!a->element)while(i<a->count&&a->values[i].tag==want)i++;if(!a->element&&i==a->count){a->element=want;return 0;}}}
    else if(v->tag==want)return 0;
    else if(numeric(*v)&&want>=FLOAT&&want<=DOUBLE&&v->tag<want){*v=number(v->number,want);return 0;}
    Text*t=concat(ascii("型別不符 / Type mismatch: "),pointer(constant(f,(u32)n>>5).ref));
    t=concat(t,ascii(" expects "));t=concat(t,ascii(code==16u?"array":names[want]));if(code&16u&&want)t=concat(t,ascii("[]"));
    t=concat(t,ascii(", got "));fail(f,concat(t,ascii(type_name(v->tag))));return 0;
}
int rt_checkHashKey(Frame*f,int n){Value v=peek(f,1);if(!numeric(v)&&v.tag!=STRING&&v.tag!=BOOL)named_failure(f,"Unusable as hash key: ",ascii(type_name(v.tag)));return 0;}
int rt_hash(Frame*f,int n){Hash*h=pointer(aqua_hash(n));u32 base=f->sp-n*2;h->count=0;for(int i=0;i<n;i++){Value k=f->values[base+i*2],v=f->values[base+i*2+1];u32 j=hash_find(h,k);if(j==h->count)h->count++;h->pairs[j]=(Pair){k,v};}f->sp=base;push(f,value(HASH,address(h),0));return 0;}
int rt_index(Frame*f,int n){Value i=pop(f),v=pop(f),out=value(NULL_VALUE,0,0);if(v.tag==ARRAY&&i.tag==INT){Array*a=pointer(v.ref);if(i.number>=0&&i.number<a->count)out=a->values[(u32)i.number];else if(a->element){failure(f,"Array index is out of bounds / 陣列索引超出範圍。");return 0;}}else if(v.tag==STRING&&i.tag==INT){Text*t=pointer(v.ref);if(i.number>=0&&i.number<t->length){Text*c=pointer(aqua_text(1));c->data[0]=t->data[(u32)i.number];out=value(STRING,address(c),0);}}else if(v.tag==HASH){if(!numeric(i)&&i.tag!=BOOL&&i.tag!=STRING){named_failure(f,"Unusable as hash key: ",ascii(type_name(i.tag)));return 0;}Hash*h=pointer(v.ref);u32 j=hash_find(h,i);if(j<h->count)out=h->pairs[j].value;}else {named_failure(f,"Index operator not supported: ",ascii(type_name(v.tag)));return 0;}push(f,out);return 0;}
int rt_checkArrayWrite(Frame*f,int n){Value i=peek(f,1),a=peek(f,2);if(a.tag==HASH){if(!numeric(i)&&i.tag!=STRING&&i.tag!=BOOL)named_failure(f,"Unusable as hash key: ",ascii(type_name(i.tag)));}else if(a.tag!=ARRAY)failure(f,"Indexed assignment requires an array or hash.");else if(i.tag!=INT)failure(f,"Array assignment index must be an integer.");else if(i.number<0||i.number>=((Array*)pointer(a.ref))->count)failure(f,"Array assignment index is out of bounds.");return 0;}
int rt_writeIndex(Frame*f,int n){Value v=pop(f);rt_checkArrayWrite(f,0);Value i=pop(f),a=pop(f);if(!f->execution->status){
    if(a.tag==HASH){Hash*h=pointer(a.ref);u32 j=hash_find(h,i);if(j==h->count)aqua_hash_resize(a.ref,h->count+1);h->pairs[j]=(Pair){i,v};if(h->exposed&&!h->dirty){h->dirty=1;h->dirty_next=pending.dirty_hashes;pending.dirty_hashes=h;}}
    else{Array*x=pointer(a.ref);if(!element_fits(f,x,v)){push(f,v);return 0;}writable_array(x,x->count);x->values[(u32)i.number]=v;if(x->exposed&&!x->dirty){x->dirty=1;x->dirty_next=pending.dirty_arrays;pending.dirty_arrays=x;}}
}push(f,v);return 0;}
int rt_member(Frame*f,int n){Value m=pop(f),out=empty;Text*t=name(f,n);
    if(m.tag==STRUCT){Struct*x=pointer(m.ref);int k=field_index(x->shape,t);if(k<0)unknown_field(f,x->shape,t);else out=x->fields[k];push(f,out);return 0;}
    if(m.tag==MODULE){Binding*b=find(pointer(m.ref),t);if(!b)named_failure(f,"Module member not found: ",t);else out=b->value;}else if(m.tag==NATIVE){MemberEntry*c=pending.host_era?member_entry(m.ref,address(t)):0;if(c&&c->era==pending.host_era&&c->native==m.ref&&c->name==address(t))out=c->value;else{int found=host_service(2,f->context,m.ref,address(t),0,0,address(&out));if(found!=0&&found!=2)named_failure(f,"Module member not found: ",t);else if(found==0&&c&&pending.host_era&&member_cacheable(out.tag)){c->native=m.ref;c->name=address(t);c->era=pending.host_era;c->value=out;}}}else named_failure(f,"Cannot access member of ",ascii(type_name(m.tag)));push(f,out);return 0;}
int rt_resolveMemberFunction(Frame*f,int n){Value m=pop(f);if(m.tag==NATIVE){Value out=empty;if(host_service(3,f->context,m.ref,0,0,0,address(&out))){failure(f,"Cannot access computed member of native value.");return 0;}m=out;}if(m.tag!=MODULE){failure(f,"Computed member requires a module.");return 0;}Value c=constant(f,n);f->execution->current=frame(f->execution,f,f->program,(u32)c.number,pointer(m.ref),f->context);return 0;}
int rt_enterLoop(Frame*f,int n){invalidate_scopes();Loop*l=allocate(sizeof(Loop),LOOPS);l->previous=f->loops;l->outer=f->scope;l->base=f->sp;l->binding=n<0?0:name(f,n);f->loops=l;f->scope=scope(f->scope);return 0;}
int rt_loopCondition(Frame*f,int n){Value v=pop(f);if(v.tag!=BOOL){failure(f,"Expected bool from for loop conditionals.");return 0;}return v.number!=0;}
int rt_nextIteration(Frame*f,int n){invalidate_scopes();Loop*l=f->loops;Value v=empty;if(l->binding){Binding*b=find(f->scope,l->binding);if(b)v=b->value;}Scope*old=f->scope;if(old->captured)f->scope=scope(l->outer);else{old->count=0;rebuild_scope_index(old);}if(l->binding)declare(f->scope,l->binding,v);return 0;}
int rt_break(Frame*f,int n){if(f->loops)f->sp=f->loops->base;return 0;}
int rt_leaveLoop(Frame*f,int n){Loop*l=f->loops;if(!l){failure(f,"Invalid compiled loop.");return 0;}invalidate_scopes();f->sp=l->base;dispose_scope(f->scope);f->scope=l->outer;f->loops=l->previous;release(l);push(f,empty);return 0;}
int rt_error(Frame*f,int n){fail(f,name(f,n));return 0;}
static void mark_ref(void*p){u32 addr=address(p);if(!p||addr<heap_start||addr>=heap_end)return;Block*b=(Block*)p-1;if(b->kind==FREE||b->marked)return;b->marked=mark_pending|1;mark_pending=addr;}
static void mark_value(Value v){if(v.tag==NATIVE){if(v.ref<65536&&!native_live[v.ref]){native_live[v.ref]=1;marked_native_ids[marked_native_count++]=v.ref;}}else if(v.tag>=STRING&&v.tag<=MODULE||v.tag==ERROR||v.tag==STRUCT||v.tag==STRUCT_TYPE)mark_ref(pointer(v.ref));}
u32 aqua_collect(void){memset(member_cache,0,sizeof member_cache);
    bytes_since_collection=0;heap_growth=0;gc_armed=0;for(Execution*e=executions;e;e=e->next)e->poll=0;
    // Marks are clear here: allocation clears them and the sweep below resets every survivor.
    for(u32 i=0;i<marked_native_count;i++)native_live[marked_native_ids[i]]=0;marked_native_count=0;mark_pending=0;
    for(Execution*e=executions;e;e=e->next)mark_ref(e);for(Pin*p=pins;p;p=p->next)mark_ref(p);for(u32 i=0;i<builtin_context_count;i++)mark_ref(builtin_scopes[builtin_contexts[i]]);for(u32 i=0;i<4;i++)if(portable_modules[i])mark_ref(portable_modules[i]);
    for(Array*a=pending.dirty_arrays;a;a=a->dirty_next)mark_ref(a);
    for(Hash*h=pending.dirty_hashes;h;h=h->dirty_next)mark_ref(h);
    while(mark_pending){void*p=pointer(mark_pending);Block*b=(Block*)p-1;mark_pending=b->marked&~1u;b->marked=1;switch(b->kind){
        case ENV:{Scope*s=p;mark_ref(s->outer);mark_ref(s->bindings);mark_ref(s->index);for(u32 i=0;i<s->count;i++){mark_ref(s->bindings[i].name);mark_value(s->bindings[i].value);}break;}
        case CLOSURES:{Closure*c=p;mark_ref(c->scope);mark_ref(c->cache);break;}
        case ARRAYS:{Array*a=p;Block*storage=(Block*)a->storage-1;
            // Recompute ownership and the append frontier from live headers only.
            // Dead longer push snapshots must not force a surviving prefix to copy.
            if(!storage->marked){a->storage->references=0;a->storage->used=0;}
            a->storage->references++;if(a->count>a->storage->used)a->storage->used=a->count;
            mark_ref(a->storage);for(u32 i=0;i<a->count;i++)mark_value(a->values[i]);break;}
        case HASHES:{Hash*h=p;mark_ref(h->pairs);mark_ref(h->index);for(u32 i=0;i<h->count;i++){mark_value(h->pairs[i].key);mark_value(h->pairs[i].value);}break;}
        case FRAMES:{Frame*f=p;mark_ref(f->parent);mark_ref(f->scope);mark_ref(f->loops);mark_ref(f->binding_cache);for(u32 i=0;i<f->sp;i++)mark_value(f->values[i]);for(u32 i=0;i<f->pending_count;i++)mark_value(f->values[f->pending_base+i]);break;}
        case LOOPS:{Loop*l=p;mark_ref(l->previous);mark_ref(l->outer);break;}
        case EXECUTIONS:{Execution*e=p;mark_ref(e->current);mark_value(e->result);break;}
        case PINS:mark_value(((Pin*)p)->value);break;
        case STRUCTS:{Struct*x=p;mark_ref(x->shape);for(u32 i=0;i<x->count;i++)mark_value(x->fields[i]);break;}
        case SHAPES:{Shape*x=p;mark_ref(x->name);for(u32 i=0;i<x->count;i++){mark_ref(x->fields[i].name);mark_ref(x->fields[i].type);}break;}
    }}u32 reclaimed=0,live=0;
    // One pass sweeps, clears survivor marks and rebuilds the segregated free lists, so tiny scopes
    // cannot consume reusable frames.
    // Adjacent free blocks coalesce, and a free run at the top of the heap returns to the bump allocator.
    memset(free_heads,0,sizeof(free_heads));free_mask=0;carving=0;Block*run=0;
    for(u32 a=heap_start;a<heap_end;){Block*b=pointer(a);a+=sizeof(Block)+b->size;
        if(b->kind!=FREE){
            if(b->kind==FOREIGN||b->marked||keep_released(b)){b->marked=0;live+=b->size;if(run){push_free(run);run=0;}continue;}
            reclaimed+=b->size;b->kind=FREE;
        }
        if(run)run->size+=sizeof(Block)+b->size;else run=b;
    }
    if(run)heap_end=address(run);
    // Allow garbage up to the live size, but no more than a quarter of the remaining headroom under the heap limit,
    // so large live sets collect more often instead of running out of memory between collections.
    {u32 limit=HEAP_LIMIT,headroom=live<limit?(limit-live)/4:0;u32 t=live<headroom?live:headroom;gc_threshold=t>8u*1024*1024?t:8u*1024*1024;}
    if(!++collection_generation)collection_generation=1;return reclaimed;
}
u32 aqua_native_live(u32 id){return id<65536?native_live[id]:0;}
