/* Aquarius value ABI 2. Portable execution; only host services cross the ABI. */
#include <stdint.h>
#include <stddef.h>

typedef uint32_t u32;
typedef struct { u32 tag, ref; double number; } Value;
enum { VOID, INT, FLOAT, DOUBLE, BOOL, STRING, ARRAY, HASH, CLOSURE, MODULE, NATIVE, NULL_VALUE, BREAK_VALUE, ERROR, BUILTIN };
enum { FREE, RAW, TEXT, VALUES, ENV, BINDINGS, CLOSURES, ARRAYS, HASHES, FRAMES, LOOPS, EXECUTIONS, PINS };
typedef struct { u32 size, next, kind, marked; } Block;
typedef struct { u32 length; uint16_t data[]; } Text;
typedef struct { Text *name; u32 hash; Value value; } Binding;
typedef struct Scope { struct Scope *outer; Binding *bindings; u32 count, capacity, captured; u32 *index,index_capacity; } Scope;
typedef struct { u32 references,capacity,used,padding; Value values[]; } ArrayStorage;
typedef struct Array { u32 count, capacity; Value *values; struct Array *dirty_next; u32 dirty, exposed; ArrayStorage *storage; } Array;
typedef struct { Value key, value; } Pair;
typedef struct { u32 count; Pair *pairs; } Hash;
typedef struct { u32 table, arity, capacity, wrap_return; Text **parameters; Value *constants; } Function;
typedef struct { u32 count; Function *functions; } Program;
typedef struct { Program *program; u32 function; Scope *scope; u32 context; } Closure;
typedef struct Loop { struct Loop *previous; Scope *outer; u32 base; Text *binding; } Loop;
typedef struct Execution Execution;
typedef struct Frame {
    struct Frame *parent; Execution *execution; Program *program; Function *function;
    Scope *scope; Loop *loops; u32 pc, sp, returned, context, capacity, pending_base, pending_count; u32 *binding_cache; Value values[];
} Frame;
struct Execution { Execution *next; Frame *current; Value result; u32 budget, status; };
_Static_assert(offsetof(Frame, values)==56, "Compiler frame ABI mismatch");
_Static_assert(offsetof(Execution, status)==28, "Compiler execution ABI mismatch");
_Static_assert(sizeof(Value)==16 && sizeof(Binding)==24 && offsetof(Binding,value)==8, "Host value/binding ABI mismatch");
typedef struct Pin { struct Pin *next; Value value; } Pin;

__attribute__((import_module("aquarius_v2"), import_name("service")))
extern int host_service(int operation, int context, int subject, int name, int arguments, int count, int output);

static u32 heap_start, heap_end, blocks, free_heads[26];
static Scope *builtin_scopes[65536];
static Execution *executions;
static Pin *pins;
static Value empty;
static u32 mark_pending;
static Array *dirty_arrays;
static unsigned char native_live[65536];
static u32 bytes_since_collection;
static u32 scope_epoch=1;
u32 aqua_collect(void);

// Bulk-memory intrinsics avoid byte-by-byte copies and preserve overlapping moves.
void *memcpy(void *d, const void *s, size_t n) { __builtin_memcpy(d,s,n);return d; }
void *memset(void *d, int x, size_t n) { __builtin_memset(d,x,n);return d; }
void *memmove(void *d, const void *s, size_t n) { __builtin_memmove(d,s,n);return d; }
static Value value(u32 tag, u32 ref, double n) { return (Value){tag,ref,n}; }
static u32 address(const void *p) { return (u32)(uintptr_t)p; }
static void *pointer(u32 p) { return (void *)(uintptr_t)p; }
static u32 allocation_class(u32 size) { return 27u-(u32)__builtin_clz(size); }
static void release(void *p) { if(p&&address(p)>=heap_start){Block*b=(Block*)p-1;if(b->kind==FREE)return;u32 bucket=allocation_class(b->size);b->kind=FREE;*(u32*)p=free_heads[bucket];free_heads[bucket]=address(b);} }
static void initialize_allocation(void *p,u32 n,u32 kind) {
    // Collection scans only committed elements and live stack operands.
    // Constructors fill value/binding buffers completely before a checkpoint.
    if(kind==VALUES||kind==BINDINGS)return;
    memset(p,0,kind==FRAMES?sizeof(Frame):n);
}
static void *allocate(u32 n,u32 kind) {
    if(n>512u*1024*1024)__builtin_trap();
    n=n<=16?16:1u<<(32u-(u32)__builtin_clz(n-1));u32 bucket=allocation_class(n);
    bytes_since_collection+=n;
    if(free_heads[bucket]){Block*b=pointer(free_heads[bucket]);free_heads[bucket]=*(u32*)(b+1);b->kind=kind;b->marked=0;initialize_allocation(b+1,b->size,kind);return b+1;}
    if(n>512u*1024*1024-heap_end-sizeof(Block))__builtin_trap();
    u32 end=heap_end+sizeof(Block)+n,available=(u32)__builtin_wasm_memory_size(0)*65536;
    if(end>available&&__builtin_wasm_memory_grow(0,(end-available+65535)/65536)==(size_t)-1)__builtin_trap();
    Block *b=pointer(heap_end);b->size=n;b->next=blocks;b->kind=kind;b->marked=0;blocks=heap_end;heap_end=end;initialize_allocation(b+1,n,kind);return b+1;
}
u32 aqua_initialize(u32 start) { if(!heap_start)heap_start=heap_end=(start+15)&~15u;return heap_start; }
u32 aqua_allocate(u32 n) { return address(allocate(n,RAW)); }
u32 aqua_heap_bytes(void) { return heap_end-heap_start; }
u32 aqua_value(void) { return address(allocate(sizeof(Value),RAW)); }
u32 aqua_text(u32 length) { Text *t=allocate(sizeof(Text)+length*2,TEXT);t->length=length;return address(t); }
static Text *ascii(const char *s) { u32 n=0;while(s[n])n++;Text*t=pointer(aqua_text(n));for(u32 i=0;i<n;i++)t->data[i]=(unsigned char)s[i];return t; }
static Text *concat(Text *a,Text *b) { Text*t=pointer(aqua_text(a->length+b->length));memcpy(t->data,a->data,a->length*2);memcpy(t->data+a->length,b->data,b->length*2);return t; }
static int text_equal(Text *a,Text*b) { if(a==b)return 1;if(!a||!b||a->length!=b->length)return 0;for(u32 i=0;i<a->length;i++)if(a->data[i]!=b->data[i])return 0;return 1; }
static const char *type_name(u32 tag) { static const char *names[]={"NULL","INTEGER","FLOAT","DOUBLE","BOOLEAN","STRING","ARRAY","HASH","FUNCTION","MODULE","NATIVE","NULL","BREAK_OBJ","ERROR"};return tag<=ERROR?names[tag]:"UNKNOWN"; }
static void fail(Frame*f,Text*t) { f->execution->result=value(ERROR,address(t),0);f->execution->status=3; }
static void failure(Frame*f,const char*s) { fail(f,ascii(s)); }
static void named_failure(Frame*f,const char*s,Text*t) { fail(f,concat(ascii(s),t)); }
static void operator_failure(Frame*f,Value a,Value b,const char*op) {
    Text*t=ascii(a.tag==b.tag?"Unknown operator: ":"Type mismatch: ");t=concat(t,ascii(type_name(a.tag)));t=concat(t,ascii(" "));t=concat(t,ascii(op));t=concat(t,ascii(" "));fail(f,concat(t,ascii(type_name(b.tag))));
}
static Text *decimal(u32 n) { char b[11];u32 count=0;do{b[count++]=(char)('0'+n%10);n/=10;}while(n);Text*t=pointer(aqua_text(count));for(u32 i=0;i<count;i++)t->data[i]=b[count-i-1];return t; }
static Scope *scope(Scope *outer) { Scope*s=allocate(sizeof(Scope),ENV);s->outer=outer;return s; }
static void invalidate_scopes(void) { if(++scope_epoch)return;scope_epoch=1;for(Execution*e=executions;e;e=e->next)for(Frame*f=e->current;f;f=f->parent)if(f->binding_cache)memset(f->binding_cache,0,(f->function->wrap_return>>1)*8); }
u32 aqua_scope(u32 outer) { Scope*s=scope(pointer(outer));s->captured=1;return address(s); }
static u32 text_hash(Text*n) { u32 h=2166136261u;for(u32 i=0;i<n->length;i++)h=(h^n->data[i])*16777619u;return h; }
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
static Value number(double n,u32 tag) { if(tag==INT)n=n>=-2147483648.0&&n<2147483648.0?(double)(int32_t)n:-2147483648.0;else if(tag==FLOAT)n=(double)(float)n;return value(tag,0,n); }
static int equal(Value a,Value b,int typed) { if(numeric(a)&&numeric(b))return (!typed||a.tag==b.tag)&&(a.number==b.number||(typed&&a.number!=a.number&&b.number!=b.number));if(a.tag!=b.tag)return 0;if(a.tag==STRING)return text_equal(pointer(a.ref),pointer(b.ref));if(a.tag==BOOL)return a.number==b.number;return a.ref==b.ref; }
static Value binary(Frame*f,int op,Value a,Value b,int compound) {
    static const char *symbols[]={"+","-","*","/","<",">","<=",">=","==","!=","&&","||"};
    if(numeric(a)&&numeric(b)){
        u32 tag=compound?a.tag:a.tag>b.tag?a.tag:b.tag;double x=a.number,y=b.number;
        switch(op){case 0:return number(x+y,tag);case 1:return number(x-y,tag);case 2:return number(x*y,tag);case 3:return number(x/y,tag);case 4:return value(BOOL,0,x<y);case 5:return value(BOOL,0,x>y);case 6:return value(BOOL,0,x<=y);case 7:return value(BOOL,0,x>=y);case 8:return value(BOOL,0,x==y);case 9:return value(BOOL,0,x!=y);}
    }
    if(op==8||op==9)return value(BOOL,0,equal(a,b,0)^(op==9));
    if((op==10||op==11)&&a.tag==BOOL&&b.tag==BOOL)return value(BOOL,0,op==10?(a.number&&b.number):(a.number||b.number));
    if(op==0&&a.tag==STRING&&b.tag==STRING)return value(STRING,address(concat(pointer(a.ref),pointer(b.ref))),0);
    operator_failure(f,a,b,symbols[op]);return empty;
}
static ArrayStorage *array_storage(u32 n) { ArrayStorage*s=allocate(sizeof(ArrayStorage)+n*sizeof(Value),VALUES);s->references=1;s->capacity=(((Block*)s-1)->size-sizeof(ArrayStorage))/sizeof(Value);s->used=n;return s; }
static void attach_storage(Array*a,ArrayStorage*s) { a->storage=s;a->capacity=s->capacity;a->values=s->values; }
static Array *array(u32 n) { Array*a=allocate(sizeof(Array),ARRAYS);a->count=n;attach_storage(a,array_storage(n));return a; }
static void writable_array(Array*a,u32 count) { if(a->storage->references==1&&count<=a->capacity)return;ArrayStorage*old=a->storage,*next=array_storage(count>a->count?count:a->count);memcpy(next->values,a->values,a->count*sizeof(Value));if(!--old->references)release(old);attach_storage(a,next); }
static Array *append_array(Array*a,Value v) { Array*b=allocate(sizeof(Array),ARRAYS);b->count=a->count+1;if(a->count==a->storage->used&&b->count<=a->capacity){a->storage->references++;attach_storage(b,a->storage);}else{attach_storage(b,array_storage(b->count));memcpy(b->values,a->values,a->count*sizeof(Value));}b->values[a->count]=v;b->storage->used=b->count;return b; }
u32 aqua_array(u32 n) { return address(array(n)); }
u32 aqua_array_values(u32 p) { Array*a=pointer(p);writable_array(a,a->count);return address(a->values); }
u32 aqua_array_count(u32 p) { return ((Array*)pointer(p))->count; }
u32 aqua_array_resize(u32 p,u32 n) { Array*a=pointer(p);writable_array(a,n);if(n>a->count)memset(a->values+a->count,0,(n-a->count)*sizeof(Value));a->count=n;a->storage->used=n;return 0; }
u32 aqua_array_expose(u32 p) { ((Array*)pointer(p))->exposed=1;return 0; }
u32 aqua_take_dirty_array(void) { Array*a=dirty_arrays;if(!a)return 0;dirty_arrays=a->dirty_next;a->dirty_next=0;a->dirty=0;return address(a); }
u32 aqua_hash(u32 n) { Hash*h=allocate(sizeof(Hash),HASHES);h->count=n;h->pairs=allocate(n*sizeof(Pair),RAW);return address(h); }
u32 aqua_hash_pairs(u32 p) { return address(((Hash*)pointer(p))->pairs); }
u32 aqua_hash_count(u32 p) { return ((Hash*)pointer(p))->count; }
u32 aqua_hash_resize(u32 p,u32 n) { Hash*h=pointer(p);if(n!=h->count){Pair*next=allocate(n*sizeof(Pair),RAW);memcpy(next,h->pairs,(n<h->count?n:h->count)*sizeof(Pair));release(h->pairs);h->pairs=next;h->count=n;}return 0; }
static Frame *frame(Execution*e,Frame*parent,Program*p,u32 id,Scope*s,u32 context) {
    if(id>=p->count)__builtin_trap();Function*fn=p->functions+id;
    Frame*f=allocate(sizeof(Frame)+fn->capacity*sizeof(Value),FRAMES);f->parent=parent;f->execution=e;f->program=p;f->function=fn;f->scope=s;f->context=context;f->capacity=fn->capacity;u32 cached=fn->wrap_return>>1;if(cached)f->binding_cache=allocate(cached*8,RAW);return f;
}
static void retain_scope(Scope*s) { for(;s&&!s->captured;s=s->outer)s->captured=1; }
static void dispose_scope(Scope*s) { if(s&&!s->captured){release(s->bindings);release(s->index);release(s);} }
static Value constant(Frame*f,u32 n) { return f->function->constants[n]; }
static Text *name(Frame*f,u32 n) { return pointer(constant(f,n).ref); }
static Binding *cached_binding(Frame*f,u32 n) { u32*c=f->binding_cache?f->binding_cache+n*2:0;if(c&&c[0]==scope_epoch&&c[1])return pointer(c[1]);Binding*b=find(f->scope,name(f,n));if(c){c[0]=scope_epoch;c[1]=address(b);}return b; }
static void release_frame(Frame*f) { release(f->binding_cache);release(f); }
u32 aqua_execute(u32 p,u32 id,u32 s,u32 context) { Execution*e=allocate(sizeof(Execution),EXECUTIONS);e->next=executions;executions=e;((Scope*)pointer(s))->captured=1;e->current=frame(e,0,pointer(p),id,pointer(s),context);return address(e); }
u32 aqua_invoke(u32 closure,u32 arguments,u32 count) {
    Closure*c=pointer(closure);Execution*e=pointer(aqua_execute(address(c->program),c->function,address(scope(c->scope)),c->context));
    e->current->scope->captured=0;
    Function*fn=e->current->function;if(count!=fn->arity){failure(e->current,"Incorrect callback argument count.");return address(e);}
    Value*a=pointer(arguments);for(u32 i=0;i<count;i++)declare(e->current->scope,fn->parameters[i],a[i]);return address(e);
}
u32 aqua_result(u32 e) { return address(&((Execution*)pointer(e))->result); }
u32 aqua_status(u32 e) { return ((Execution*)pointer(e))->status; }
u32 aqua_resume(u32 e,u32 v) { Execution*x=pointer(e);x->status=0;x->current->pending_count=0;push(x->current,*(Value*)pointer(v));return 0; }
u32 aqua_release_execution(u32 ptr) { Execution*e=pointer(ptr);Execution**p=&executions;while(*p&&*p!=e)p=&(*p)->next;if(*p)*p=e->next;for(Frame*f=e->current;f;){Frame*next=f->parent;release_frame(f);f=next;}release(e);return 0; }
u32 aqua_pin(u32 v) { Pin*p=allocate(sizeof(Pin),PINS);p->value=*(Value*)pointer(v);p->next=pins;pins=p;return address(&p->value); }
u32 aqua_unpin(u32 v) { Pin**p=&pins;while(*p&&address(&(*p)->value)!=v)p=&(*p)->next;if(*p){Pin*x=*p;*p=x->next;release(x);}return 0; }
u32 aqua_run(u32 ptr,u32 budget) {
    Execution*e=pointer(ptr);if(e->status==2||e->status==3)return e->status;e->budget=budget;e->status=0;
    while(e->current){Frame*f=e->current;int next=((int(*)(Frame*))(uintptr_t)f->function->table)(f);
        if(e->status==3)return 3;
        if(next<0){Value result=f->sp?pop(f):empty;Frame*parent=f->parent;dispose_scope(f->scope);release_frame(f);e->current=parent;if(!parent){e->result=result;e->status=4;return 4;}push(parent,result);}
        else f->pc=(u32)next;
        if(e->status==2)return 2;if(e->status==1)return 1;
    }e->status=4;return 4;
}
int rt_pc(Frame*f,int unused) { return (int)f->pc; }
int rt_failed(Frame*f,int unused) { return f->execution->status==3; }
int rt_checkpoint(Frame*f,int unused) { if(bytes_since_collection>=8u*1024*1024)aqua_collect();if(!f->execution->budget||!--f->execution->budget){f->execution->status=1;return 1;}return 0; }
int rt_constant(Frame*f,int n) { push(f,constant(f,n));return 0; }
int rt_void(Frame*f,int n) { push(f,empty);return 0; }
int rt_null(Frame*f,int n) { push(f,value(NULL_VALUE,0,0));return 0; }
int rt_pop(Frame*f,int n) { pop(f);return 0; }
int rt_duplicate(Frame*f,int n) { push(f,peek(f,1));return 0; }
int rt_load(Frame*f,int n) { Binding*b=cached_binding(f,n);if(!b&&f->context<65536&&builtin_scopes[f->context])b=find_owned(builtin_scopes[f->context],name(f,n));if(b)push(f,b->value);else {Value v=empty;int status=host_service(0,f->context,0,address(name(f,n)),0,0,address(&v));if(status)named_failure(f,"Identifier not found: ",name(f,n));else {if(f->context<65536){if(!builtin_scopes[f->context])builtin_scopes[f->context]=scope(0);declare(builtin_scopes[f->context],name(f,n),v);}push(f,v);}}return 0; }
int rt_declare(Frame*f,int n) { declare(f->scope,name(f,n),pop(f));push(f,empty);return 0; }
int rt_assign(Frame*f,int n) { Value v=pop(f);pop(f);Binding*b=cached_binding(f,n);if(!b)named_failure(f,"Identifier not found: ",name(f,n));else b->value=v;push(f,empty);return 0; }
int rt_compoundAssign(Frame*f,int n) { Value c=constant(f,n),r=pop(f);pop(f);Binding*b=cached_binding(f,n);if(!b)named_failure(f,"Identifier not found: ",pointer(c.ref));else b->value=binary(f,(int)c.number,b->value,r,1);push(f,empty);return 0; }
#define BINARY(NAME,OP) int rt_##NAME(Frame*f,int n){Value r=pop(f),l=pop(f);push(f,binary(f,OP,l,r,0));return 0;}
BINARY(add,0) BINARY(subtract,1) BINARY(multiply,2) BINARY(divide,3) BINARY(less,4) BINARY(greater,5) BINARY(lessEqual,6) BINARY(greaterEqual,7) BINARY(equal,8) BINARY(notEqual,9) BINARY(and,10) BINARY(or,11)
int rt_negate(Frame*f,int n) { Value v=pop(f);if(numeric(v))push(f,number(-v.number,v.tag));else named_failure(f,"Unknown operator: -",ascii(type_name(v.tag)));return 0; }
int rt_not(Frame*f,int n) { Value v=pop(f);push(f,value(BOOL,0,v.tag==BOOL?!v.number:v.tag==NULL_VALUE));return 0; }
static int increment(Frame*f,int n,int prefix) { Binding*b=cached_binding(f,n);if(!b){named_failure(f,"Identifier not found: ",name(f,n));return 0;}Value old=b->value;if(!numeric(old)){failure(f,"Increment requires a number.");return 0;}b->value=number(old.number+1,old.tag);push(f,prefix?b->value:old);return 0; }
int rt_incrementPrefix(Frame*f,int n){return increment(f,n,1);}int rt_incrementPostfix(Frame*f,int n){return increment(f,n,0);}
int rt_truth(Frame*f,int n) { Value v=pop(f);return v.tag==BOOL?v.number!=0:v.tag!=NULL_VALUE; }
int rt_isBreak(Frame*f,int n) { return peek(f,1).tag==BREAK_VALUE; }
int rt_closure(Frame*f,int n) { Value c=constant(f,n);Closure*x=allocate(sizeof(Closure),CLOSURES);x->program=f->program;x->function=(u32)c.number;x->scope=f->scope;x->context=f->context;retain_scope(f->scope);push(f,value(CLOSURE,address(x),0));return 0; }
int rt_call(Frame*f,int n) {
    if(n<0||(u32)n>=f->sp){failure(f,"Invalid compiled call.");return 0;}u32 base=f->sp-n-1;Value callee=f->values[base];Value*args=f->values+base+1;
    if(callee.tag==BUILTIN){Value out=empty;if(n!=(callee.ref==4?2:1)){failure(f,"Incorrect builtin argument count.");return 0;}Value a=args[0];
        if(callee.ref==1){if(a.tag==STRING)out=number(((Text*)pointer(a.ref))->length,INT);else if(a.tag==ARRAY)out=number(((Array*)pointer(a.ref))->count,INT);else failure(f,"Length requires string or array.");}
        else if(a.tag!=ARRAY)failure(f,"Builtin requires an array.");else{Array*x=pointer(a.ref);if(callee.ref==2)out=x->count?x->values[x->count-1]:value(NULL_VALUE,0,0);
            else if(callee.ref==3){if(!x->count)out=value(NULL_VALUE,0,0);else{Array*y=array(x->count-1);memcpy(y->values,x->values+1,y->count*sizeof(Value));out=value(ARRAY,address(y),0);}}
            else{Array*y=append_array(x,args[1]);out=value(ARRAY,address(y),0);}}
        f->sp=base;push(f,out);}
    else if(callee.tag==CLOSURE){Closure*c=pointer(callee.ref);Function*fn=c->program->functions+c->function;if(fn->arity!=(u32)n){Text*t=concat(ascii("Function expects "),decimal(fn->arity));t=concat(t,ascii(" arguments, got "));t=concat(t,decimal(n));fail(f,concat(t,ascii(".")));return 0;}Scope*s=scope(c->scope);for(int i=0;i<n;i++)declare(s,fn->parameters[i],args[i]);f->sp=base;f->execution->current=frame(f->execution,f,c->program,c->function,s,c->context);}
    else if(callee.tag==NATIVE){Value out=empty;int pending=host_service(1,f->context,callee.ref,0,address(args),n,address(&out));f->sp=base;if(pending==1){f->pending_base=base;f->pending_count=n+1;f->execution->status=2;}else push(f,out);}
    else named_failure(f,"Not a function: ",ascii(type_name(callee.tag)));return 0;
}
int rt_returned(Frame*f,int n){f->returned=1;return 0;}
int rt_array(Frame*f,int n){Array*a=array(n);for(int i=n-1;i>=0;i--)a->values[i]=pop(f);push(f,value(ARRAY,address(a),0));return 0;}
int rt_checkHashKey(Frame*f,int n){Value v=peek(f,1);if(!numeric(v)&&v.tag!=STRING&&v.tag!=BOOL)named_failure(f,"Unusable as hash key: ",ascii(type_name(v.tag)));return 0;}
int rt_hash(Frame*f,int n){Hash*h=pointer(aqua_hash(n));u32 count=0,base=f->sp-n*2;for(int i=0;i<n;i++){Value k=f->values[base+i*2],v=f->values[base+i*2+1];u32 j=0;while(j<count&&!equal(h->pairs[j].key,k,1))j++;h->pairs[j]=(Pair){k,v};if(j==count)count++;}h->count=count;f->sp=base;push(f,value(HASH,address(h),0));return 0;}
int rt_index(Frame*f,int n){Value i=pop(f),v=pop(f),out=value(NULL_VALUE,0,0);if(v.tag==ARRAY&&i.tag==INT){Array*a=pointer(v.ref);if(i.number>=0&&i.number<a->count)out=a->values[(u32)i.number];}else if(v.tag==HASH){if(!numeric(i)&&i.tag!=BOOL&&i.tag!=STRING){named_failure(f,"Unusable as hash key: ",ascii(type_name(i.tag)));return 0;}Hash*h=pointer(v.ref);for(u32 j=0;j<h->count;j++)if(equal(i,h->pairs[j].key,1)){out=h->pairs[j].value;break;}}else {named_failure(f,"Index operator not supported: ",ascii(type_name(v.tag)));return 0;}push(f,out);return 0;}
int rt_checkArrayWrite(Frame*f,int n){Value i=peek(f,1),a=peek(f,2);if(a.tag!=ARRAY)failure(f,"Indexed assignment requires an array.");else if(i.tag!=INT)failure(f,"Array assignment index must be an integer.");else if(i.number<0||i.number>=((Array*)pointer(a.ref))->count)failure(f,"Array assignment index is out of bounds.");return 0;}
int rt_writeIndex(Frame*f,int n){Value v=pop(f);rt_checkArrayWrite(f,0);Value i=pop(f),a=pop(f);if(!f->execution->status){Array*x=pointer(a.ref);writable_array(x,x->count);x->values[(u32)i.number]=v;if(x->exposed&&!x->dirty){x->dirty=1;x->dirty_next=dirty_arrays;dirty_arrays=x;}}push(f,v);return 0;}
int rt_member(Frame*f,int n){Value m=pop(f),out=empty;Text*t=name(f,n);if(m.tag==MODULE){Binding*b=find(pointer(m.ref),t);if(!b)named_failure(f,"Module member not found: ",t);else out=b->value;}else if(m.tag==NATIVE){if(host_service(2,f->context,m.ref,address(t),0,0,address(&out)))named_failure(f,"Module member not found: ",t);}else named_failure(f,"Cannot access member of ",ascii(type_name(m.tag)));push(f,out);return 0;}
int rt_resolveMemberFunction(Frame*f,int n){Value m=pop(f);if(m.tag==NATIVE){Value out=empty;if(host_service(3,f->context,m.ref,0,0,0,address(&out))){failure(f,"Cannot access computed member of native value.");return 0;}m=out;}if(m.tag!=MODULE){failure(f,"Computed member requires a module.");return 0;}Value c=constant(f,n);f->execution->current=frame(f->execution,f,f->program,(u32)c.number,pointer(m.ref),f->context);return 0;}
int rt_enterLoop(Frame*f,int n){invalidate_scopes();Loop*l=allocate(sizeof(Loop),LOOPS);l->previous=f->loops;l->outer=f->scope;l->base=f->sp;l->binding=n<0?0:name(f,n);f->loops=l;f->scope=scope(f->scope);return 0;}
int rt_loopCondition(Frame*f,int n){Value v=pop(f);if(v.tag!=BOOL){failure(f,"Expected bool from for loop conditionals.");return 0;}return v.number!=0;}
int rt_nextIteration(Frame*f,int n){invalidate_scopes();Loop*l=f->loops;Value v=empty;if(l->binding){Binding*b=find(f->scope,l->binding);if(b)v=b->value;}Scope*old=f->scope;if(old->captured)f->scope=scope(l->outer);else{old->count=0;rebuild_scope_index(old);}if(l->binding)declare(f->scope,l->binding,v);return 0;}
int rt_break(Frame*f,int n){if(f->loops)f->sp=f->loops->base;return 0;}
int rt_leaveLoop(Frame*f,int n){Loop*l=f->loops;if(!l){failure(f,"Invalid compiled loop.");return 0;}invalidate_scopes();f->sp=l->base;dispose_scope(f->scope);f->scope=l->outer;f->loops=l->previous;release(l);push(f,empty);return 0;}
int rt_error(Frame*f,int n){fail(f,name(f,n));return 0;}
static void mark_ref(void*p){u32 addr=address(p);if(!p||addr<heap_start||addr>=heap_end)return;Block*b=(Block*)p-1;if(b->kind==FREE||b->marked)return;b->marked=mark_pending|1;mark_pending=addr;}
static void mark_value(Value v){if(v.tag==NATIVE){if(v.ref<65536)native_live[v.ref]=1;}else if(v.tag>=STRING&&v.tag<=MODULE||v.tag==ERROR)mark_ref(pointer(v.ref));}
u32 aqua_collect(void){
    bytes_since_collection=0;
    for(Block*b=pointer(blocks);b;b=pointer(b->next))b->marked=0;memset(native_live,0,sizeof(native_live));mark_pending=0;
    for(Execution*e=executions;e;e=e->next)mark_ref(e);for(Pin*p=pins;p;p=p->next)mark_ref(p);for(u32 i=0;i<65536;i++)if(builtin_scopes[i])mark_ref(builtin_scopes[i]);
    for(Array*a=dirty_arrays;a;a=a->dirty_next)mark_ref(a);
    while(mark_pending){void*p=pointer(mark_pending);Block*b=(Block*)p-1;mark_pending=b->marked&~1u;b->marked=1;switch(b->kind){
        case ENV:{Scope*s=p;mark_ref(s->outer);mark_ref(s->bindings);mark_ref(s->index);for(u32 i=0;i<s->count;i++){mark_ref(s->bindings[i].name);mark_value(s->bindings[i].value);}break;}
        case CLOSURES:{Closure*c=p;mark_ref(c->scope);break;}
        case ARRAYS:{Array*a=p;mark_ref(a->storage);for(u32 i=0;i<a->count;i++)mark_value(a->values[i]);break;}
        case HASHES:{Hash*h=p;mark_ref(h->pairs);for(u32 i=0;i<h->count;i++){mark_value(h->pairs[i].key);mark_value(h->pairs[i].value);}break;}
        case FRAMES:{Frame*f=p;mark_ref(f->parent);mark_ref(f->scope);mark_ref(f->loops);mark_ref(f->binding_cache);for(u32 i=0;i<f->sp;i++)mark_value(f->values[i]);for(u32 i=0;i<f->pending_count;i++)mark_value(f->values[f->pending_base+i]);break;}
        case LOOPS:{Loop*l=p;mark_ref(l->previous);mark_ref(l->outer);break;}
        case EXECUTIONS:{Execution*e=p;mark_ref(e->current);mark_value(e->result);break;}
        case PINS:mark_value(((Pin*)p)->value);break;
    }}u32 reclaimed=0;for(Block*b=pointer(blocks);b;b=pointer(b->next))if(b->kind==ARRAYS&&!b->marked){Array*a=(void*)(b+1);a->storage->references--;}
    for(Block*b=pointer(blocks);b;b=pointer(b->next))if(b->kind!=FREE&&!b->marked){reclaimed+=b->size;b->kind=FREE;}
    // Segregated free lists prevent tiny scopes from consuming reusable frames.
    memset(free_heads,0,sizeof(free_heads));
    for(Block*b=pointer(blocks);b;b=pointer(b->next))if(b->kind==FREE){u32 bucket=allocation_class(b->size);*(u32*)(b+1)=free_heads[bucket];free_heads[bucket]=address(b);}return reclaimed;
}
u32 aqua_native_live(u32 id){return id<65536?native_live[id]:0;}
