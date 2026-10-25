/* Builtin IDs 6..19: portable array library. Included after runtime value/allocation helpers. */
#define ARRAY_LIMIT 33554431u
static Text *utf16(const uint16_t *s) {
    u32 n=0;while(s[n])n++;Text*t=pointer(aqua_text(n));memcpy(t->data,s,n*2);return t;
}
static int text_is(Text*t,const uint16_t*s) {
    u32 i=0;while(i<t->length&&s[i]&&t->data[i]==s[i])i++;return i==t->length&&!s[i];
}
/* The Array library: ids, bilingual names and arities live only here; the host never implements or lists them. */
#define ARRAY_FIRST_ID 6u
#define ARRAY_COUNT 14u
static const uint16_t *const array_module_names[2]={u"Array",u"陣列"};
static const uint16_t *const array_names[ARRAY_COUNT][2]={{u"Make",u"建立"},{u"Len",u"長度"},{u"Cap",u"容量"},{u"Grow",u"擴充"},{u"Append",u"追加"},{u"AppendArray",u"追加陣列"},{u"Copy",u"複製"},{u"Clone",u"克隆"},{u"Slice",u"切片"},{u"Fill",u"填入"},{u"Delete",u"刪除"},{u"Reverse",u"反轉"},{u"Index",u"尋找"},{u"Contains",u"包含"}};
static const unsigned char array_minimum[ARRAY_COUNT]={2,1,1,2,2,2,2,1,3,2,3,1,2,2};
static const unsigned char array_maximum[ARRAY_COUNT]={3,1,1,2,2,2,5,1,3,2,3,1,2,2};
static Scope *array_library(void) {
    Scope*s=scope(0);s->captured=1;for(u32 i=0;i<ARRAY_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(array_names[i][j]),value(BUILTIN,ARRAY_FIRST_ID+i,0));return s;
}
static void array_changed(Array*a) {
    if(a->exposed&&!a->dirty){a->dirty=1;a->dirty_next=pending.dirty_arrays;pending.dirty_arrays=a;}
}
static int array_size(Frame*f,Value v,u32*out) {
    if(v.tag!=INT||v.number<0||v.number>ARRAY_LIMIT){failure(f,"Array size/index must be a nonnegative integer within the Wasm array limit.");return 0;}
    *out=(u32)v.number;return 1;
}
static Value array_call(Frame*f,u32 id,Value*args,u32 n) {
    if(id<ARRAY_FIRST_ID||id>=ARRAY_FIRST_ID+ARRAY_COUNT||n<array_minimum[id-ARRAY_FIRST_ID]||n>array_maximum[id-ARRAY_FIRST_ID]){failure(f,"Incorrect Array argument count.");return empty;}
    u32 count=0,start=0,end=0;
    if(id==6){
        u32 capacity;if(!array_size(f,args[0],&count))return empty;capacity=count;
        if(n==3&&!array_size(f,args[2],&capacity))return empty;
        if(capacity<count){failure(f,"Array capacity must be at least its length.");return empty;}
        Array*a=array(capacity);a->count=count;a->storage->used=count;
        for(u32 i=0;i<count;i++)a->values[i]=args[1];return value(ARRAY,address(a),0);
    }
    if(args[0].tag!=ARRAY){failure(f,"Array function requires an array.");return empty;}
    Array*a=pointer(args[0].ref);
    if(id==7)return number(a->count,INT);
    if(id==8)return number(a->capacity,INT);
    if(id==9){
        if(!array_size(f,args[1],&count))return empty;
        if(count>ARRAY_LIMIT-a->count){failure(f,"Array capacity exceeds the Wasm array limit.");return empty;}
        if(a->count+count>a->capacity)writable_array(a,a->count+count);return args[0];
    }
    if(id==10||id==11){
        Array*b=0;count=1;
        if(id==11){if(args[1].tag!=ARRAY){failure(f,"Array.AppendArray requires an array source.");return empty;}b=pointer(args[1].ref);count=b->count;}
        if(count>ARRAY_LIMIT-a->count){failure(f,"Array length exceeds the Wasm array limit.");return empty;}
        if(!count)return args[0];start=a->count;writable_array(a,start+count);
        if(b)memmove(a->values+start,b->values,count*sizeof(Value));else a->values[start]=args[1];
        a->count=start+count;a->storage->used=a->count;array_changed(a);return args[0];
    }
    if(id==12){
        if(args[1].tag!=ARRAY){failure(f,"Array.Copy requires an array source.");return empty;}
        Array*b=pointer(args[1].ref);
        if(n>=3&&!array_size(f,args[2],&start))return empty;
        if(n>=4&&!array_size(f,args[3],&end))return empty;
        if(start>a->count||end>b->count){failure(f,"Array.Copy index is out of bounds.");return empty;}
        count=a->count-start;if(count>b->count-end)count=b->count-end;
        if(n==5){u32 requested;if(!array_size(f,args[4],&requested))return empty;if(requested<count)count=requested;}
        if(count){writable_array(a,a->count);memmove(a->values+start,b->values+end,count*sizeof(Value));array_changed(a);}return number(count,INT);
    }
    if(id==13||id==14){
        end=a->count;
        if(id==14&&(!array_size(f,args[1],&start)||!array_size(f,args[2],&end)))return empty;
        if(start>end||end>a->count){failure(f,"Array slice range is out of bounds.");return empty;}
        Array*b=array(end-start);memcpy(b->values,a->values+start,b->count*sizeof(Value));return value(ARRAY,address(b),0);
    }
    if(id==15){
        if(a->count){writable_array(a,a->count);for(u32 i=0;i<a->count;i++)a->values[i]=args[1];array_changed(a);}return args[0];
    }
    if(id==16){
        if(!array_size(f,args[1],&start)||!array_size(f,args[2],&end))return empty;
        if(start>end||end>a->count){failure(f,"Array delete range is out of bounds.");return empty;}
        if(start!=end){writable_array(a,a->count);memmove(a->values+start,a->values+end,(a->count-end)*sizeof(Value));
            count=a->count-(end-start);memset(a->values+count,0,(a->count-count)*sizeof(Value));
            a->count=count;a->storage->used=count;array_changed(a);}return args[0];
    }
    if(id==17){
        if(a->count>1){writable_array(a,a->count);for(u32 i=0,j=a->count-1;i<j;i++,j--){Value v=a->values[i];a->values[i]=a->values[j];a->values[j]=v;}array_changed(a);}return args[0];
    }
    for(u32 i=0;i<a->count;i++)if(equal(a->values[i],args[1],0))return id==18?number(i,INT):value(BOOL,0,1);
    return id==18?number(-1,INT):value(BOOL,0,0);
}
