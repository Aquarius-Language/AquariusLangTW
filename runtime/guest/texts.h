/* Builtin IDs 30..45: portable text (string) library. Included after hashes.h.
 * Strings are UTF-16 code-unit sequences; indexes and lengths use code units,
 * matching 長度 and string indexing. Number formatting follows JavaScript's
 * Number#toString (shortest round trip, closest digits) so 轉文字 and the
 * browser 印出 agree. */
typedef struct { uint16_t *data; u32 length, capacity; } TextBuilder;
static void tb_reserve(TextBuilder*b,u32 extra) {
    if(b->length+extra<=b->capacity)return;
    u32 capacity=b->capacity?b->capacity:32;while(capacity<b->length+extra)capacity*=2;
    uint16_t*next=allocate(capacity*2,RAW);if(b->length)memcpy(next,b->data,b->length*2);release(b->data);b->data=next;b->capacity=capacity;
}
static void tb_unit(TextBuilder*b,uint16_t c) { tb_reserve(b,1);b->data[b->length++]=c; }
static void tb_units(TextBuilder*b,const uint16_t*s,u32 n) { if(!n)return;tb_reserve(b,n);memcpy(b->data+b->length,s,n*2);b->length+=n; }
static void tb_ascii(TextBuilder*b,const char*s) { while(*s)tb_unit(b,(unsigned char)*s++); }
static void tb_text(TextBuilder*b,Text*t) { tb_units(b,t->data,t->length); }
static Text *tb_finish(TextBuilder*b) { Text*t=pointer(aqua_text(b->length));if(b->length)memcpy(t->data,b->data,b->length*2);release(b->data);b->data=0;b->length=b->capacity=0;return t; }
static Value text_value(Text*t) { return value(STRING,address(t),0); }
static Text *text_slice(Text*t,u32 start,u32 end) { Text*s=pointer(aqua_text(end-start));memcpy(s->data,t->data+start,(end-start)*2);return s; }
static int text_at(Text*t,u32 at,Text*needle) {
    if(at+needle->length>t->length)return 0;
    for(u32 i=0;i<needle->length;i++)if(t->data[at+i]!=needle->data[i])return 0;return 1;
}
static int text_find(Text*t,Text*needle,u32 from) {
    if(needle->length>t->length)return -1;
    for(u32 i=from;i+needle->length<=t->length;i++)if(text_at(t,i,needle))return (int)i;return -1;
}

/* ---- Exact shortest round-trip digits (Burger & Dybvig free-format algorithm with bignums) ----
 * Produces the shortest digit string that reads back as the same value and, among
 * those, the one closest to it — the digits ECMAScript Number#toString prints. */
typedef struct { u32 n; uint32_t w[40]; } Big;   /* little-endian base-2^32, up to 1280 bits */
static void big_set(Big*a,uint64_t v) { a->n=0;while(v){a->w[a->n++]=(uint32_t)v;v>>=32;} }
static void big_mul_small(Big*a,uint32_t m) {
    uint64_t carry=0;for(u32 i=0;i<a->n;i++){uint64_t t=(uint64_t)a->w[i]*m+carry;a->w[i]=(uint32_t)t;carry=t>>32;}
    if(carry)a->w[a->n++]=(uint32_t)carry;
}
static void big_shift(Big*a,u32 bits) {
    if(!a->n)return;u32 words=bits/32,rest=bits%32;
    if(rest){uint32_t carry=0;for(u32 i=0;i<a->n;i++){uint32_t v=a->w[i];a->w[i]=(v<<rest)|carry;carry=v>>(32-rest);}if(carry)a->w[a->n++]=carry;}
    if(words){for(int i=(int)a->n-1;i>=0;i--)a->w[i+words]=a->w[i];for(u32 i=0;i<words;i++)a->w[i]=0;a->n+=words;}
}
static int big_compare(const Big*a,const Big*b) {
    if(a->n!=b->n)return a->n<b->n?-1:1;
    for(int i=(int)a->n-1;i>=0;i--)if(a->w[i]!=b->w[i])return a->w[i]<b->w[i]?-1:1;return 0;
}
static void big_add(Big*out,const Big*a,const Big*b) {
    const Big*x=a->n>=b->n?a:b,*y=a->n>=b->n?b:a;uint64_t carry=0;u32 n=x->n;
    for(u32 i=0;i<n;i++){uint64_t t=(uint64_t)x->w[i]+(i<y->n?y->w[i]:0)+carry;out->w[i]=(uint32_t)t;carry=t>>32;}
    out->n=n;if(carry)out->w[out->n++]=(uint32_t)carry;
}
static void big_sub(Big*a,const Big*b) {   /* a -= b, requires a >= b */
    int64_t borrow=0;for(u32 i=0;i<a->n;i++){int64_t t=(int64_t)a->w[i]-(i<b->n?b->w[i]:0)-borrow;borrow=t<0;a->w[i]=(uint32_t)(t+(borrow?4294967296LL:0));}
    while(a->n&&!a->w[a->n-1])a->n--;
}
/* value = f * 2^e; lower_closer marks exact powers of two above the smallest normal.
 * Exact ties between two shortest candidates go to the even digit (Number#toString),
 * or upward for single precision (the hosts print floats through toPrecision). */
static int shortest_digits(uint64_t f,int e,int lower_closer,int ties_up,char*digits,int*k) {
    Big r,s,mplus,mminus;int even=!(f&1);
    if(e>=0){
        big_set(&r,f);big_shift(&r,(u32)e+(lower_closer?2:1));big_set(&s,lower_closer?4:2);
        big_set(&mplus,1);big_shift(&mplus,(u32)e+(lower_closer?1:0));big_set(&mminus,1);big_shift(&mminus,(u32)e);
    } else {
        big_set(&r,f);big_shift(&r,lower_closer?2:1);big_set(&s,1);big_shift(&s,(u32)(-e)+(lower_closer?2:1));
        big_set(&mplus,lower_closer?2:1);big_set(&mminus,1);
    }
    int bits=0;for(uint64_t t=f;t;t>>=1)bits++;
    int estimate=0;{double guess=(e+bits-1)*0.30102999566398114-1e-10;estimate=(int)guess;if(guess>estimate)estimate++;}
    if(estimate>=0)for(int i=0;i<estimate;i++)big_mul_small(&s,10);
    else for(int i=0;i<-estimate;i++){big_mul_small(&r,10);big_mul_small(&mplus,10);big_mul_small(&mminus,10);}
    Big high;big_add(&high,&r,&mplus);
    if(big_compare(&high,&s)>=(even?0:1))estimate++;
    else{big_mul_small(&r,10);big_mul_small(&mplus,10);big_mul_small(&mminus,10);}
    *k=estimate;int length=0;
    for(;;){
        int digit=0;while(big_compare(&r,&s)>=0){big_sub(&r,&s);digit++;}
        int low=even?big_compare(&r,&mminus)<=0:big_compare(&r,&mminus)<0;
        big_add(&high,&r,&mplus);int up=even?big_compare(&high,&s)>=0:big_compare(&high,&s)>0;
        if(!low&&!up){digits[length++]=(char)('0'+digit);big_mul_small(&r,10);big_mul_small(&mplus,10);big_mul_small(&mminus,10);continue;}
        if(low&&up){Big twice=r;big_shift(&twice,1);int cmp=big_compare(&twice,&s);if(cmp>0||(cmp==0&&(ties_up||(digit&1))))digit++;}
        else if(up)digit++;
        digits[length++]=(char)('0'+digit);return length;
    }
}
static void format_double(TextBuilder*b,double x,int single) {
    if(x!=x){tb_ascii(b,"NaN");return;}
    if(x==0){tb_unit(b,'0');return;}
    if(x<0){tb_unit(b,'-');x=-x;}
    if(x>1.7976931348623157e308){tb_ascii(b,"Infinity");return;}
    char digits[40];int length=0,k=0;
    if(single){
        float s=(float)x;uint32_t bits;memcpy(&bits,&s,4);
        uint32_t mantissa=bits&0x7FFFFFu,biased=(bits>>23)&0xFFu;
        if(biased)length=shortest_digits(mantissa|0x800000u,(int)biased-150,mantissa==0&&biased>1,1,digits,&k);
        else length=shortest_digits(mantissa,-149,0,1,digits,&k);
    } else {
        uint64_t bits;memcpy(&bits,&x,8);
        uint64_t mantissa=bits&0xFFFFFFFFFFFFFull;u32 biased=(u32)(bits>>52)&0x7FFu;
        if(biased)length=shortest_digits(mantissa|0x10000000000000ull,(int)biased-1075,mantissa==0&&biased>1,0,digits,&k);
        else length=shortest_digits(mantissa,-1074,0,0,digits,&k);
    }
    int point=k;
    if(length<=point&&point<=21){for(int i=0;i<length;i++)tb_unit(b,(uint16_t)digits[i]);for(int i=length;i<point;i++)tb_unit(b,'0');return;}
    if(0<point&&point<=21){for(int i=0;i<length;i++){if(i==point)tb_unit(b,'.');tb_unit(b,(uint16_t)digits[i]);}return;}
    if(-6<point&&point<=0){tb_ascii(b,"0.");for(int i=point;i<0;i++)tb_unit(b,'0');for(int i=0;i<length;i++)tb_unit(b,(uint16_t)digits[i]);return;}
    tb_unit(b,(uint16_t)digits[0]);
    if(length>1){tb_unit(b,'.');for(int i=1;i<length;i++)tb_unit(b,(uint16_t)digits[i]);}
    int e=point-1;tb_unit(b,'e');tb_unit(b,e<0?'-':'+');if(e<0)e=-e;
    char rev[8];int r=0;do{rev[r++]=(char)('0'+e%10);e/=10;}while(e);while(r)tb_unit(b,(uint16_t)rev[--r]);
}
static void format_int(TextBuilder*b,double n) {
    long long v=(long long)n;if(v<0){tb_unit(b,'-');v=-v;}
    char rev[24];int r=0;do{rev[r++]=(char)('0'+v%10);v/=10;}while(v);while(r)tb_unit(b,(uint16_t)rev[--r]);
}
/* Matches the hosts' 印出 / inspect formatting. */
static void format_value(TextBuilder*b,Value v,u32 depth) {
    if(depth>64){tb_ascii(b,"...");return;}
    switch(v.tag){
    case STRING: tb_text(b,pointer(v.ref));return;
    case INT: format_int(b,v.number);return;
    case FLOAT: format_double(b,v.number,1);return;
    case DOUBLE: format_double(b,v.number,0);return;
    case BOOL: tb_unit(b,v.number?0x771F:0x5047);return; /* 真 / 假 */
    case NULL_VALUE: tb_ascii(b,"null");return;
    case ARRAY: {
        Array*a=pointer(v.ref);tb_unit(b,'[');
        for(u32 i=0;i<a->count;i++){if(i)tb_ascii(b,", ");format_value(b,a->values[i],depth+1);}
        tb_unit(b,']');return;
    }
    case HASH: {
        Hash*h=pointer(v.ref);tb_unit(b,'{');
        for(u32 i=0;i<h->count;i++){if(i)tb_ascii(b,", ");format_value(b,h->pairs[i].key,depth+1);tb_ascii(b,": ");format_value(b,h->pairs[i].value,depth+1);}
        tb_unit(b,'}');return;
    }
    case MODULE: case NATIVE: tb_ascii(b,"module");return;
    case CLOSURE: case BUILTIN: tb_ascii(b,"function");return;
    default: return;
    }
}
static Text *to_text(Value v) { TextBuilder b={0};format_value(&b,v,0);return tb_finish(&b); }

/* The String library: ids, bilingual names and arities live only here; the host never implements or lists them. */
#define TEXT_FIRST_ID 30u
#define TEXT_COUNT 16u
static const uint16_t *const text_module_names[2]={u"String",u"字串"};
static const uint16_t *const text_names[TEXT_COUNT][2]={{u"From",u"轉文字"},{u"Slice",u"切片"},{u"Find",u"尋找"},{u"Contains",u"包含"},{u"StartsWith",u"開頭是"},{u"EndsWith",u"結尾是"},{u"Split",u"分割"},{u"Join",u"合併"},{u"Replace",u"取代"},{u"Trim",u"修剪"},{u"Upper",u"轉大寫"},{u"Lower",u"轉小寫"},{u"Repeat",u"重複"},{u"Code",u"字元碼"},{u"FromCode",u"從字元碼"},{u"ParseInt",u"解析整數"}};
static const unsigned char text_minimum[TEXT_COUNT]={1,3,2,2,2,2,2,2,3,1,1,1,2,2,1,1};
static const unsigned char text_maximum[TEXT_COUNT]={1,3,2,2,2,2,2,2,3,1,1,1,2,2,1,1};
static Scope *text_library(void) {
    Scope*s=scope(0);s->captured=1;for(u32 i=0;i<TEXT_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(text_names[i][j]),value(BUILTIN,TEXT_FIRST_ID+i,0));return s;
}
static int text_space(uint16_t c) {
    return c==' '||(c>=9&&c<=13)||c==0xA0||c==0x1680||(c>=0x2000&&c<=0x200A)||c==0x2028||c==0x2029||c==0x202F||c==0x205F||c==0x3000||c==0xFEFF;
}
static uint16_t text_case(uint16_t c,int upper) {
    if(upper){if((c>='a'&&c<='z')||(c>=0xFF41&&c<=0xFF5A))return (uint16_t)(c-32);}
    else if((c>='A'&&c<='Z')||(c>=0xFF21&&c<=0xFF3A))return (uint16_t)(c+32);
    return c;
}
static int text_index(Frame*f,Value v,u32 limit,u32*out) {
    if(v.tag!=INT||v.number<0||v.number>limit){failure(f,"String index must be an integer within the string.");return 0;}
    *out=(u32)v.number;return 1;
}
static Value text_call(Frame*f,u32 id,Value*args,u32 n) {
    if(id<TEXT_FIRST_ID||id>=TEXT_FIRST_ID+TEXT_COUNT||n<text_minimum[id-TEXT_FIRST_ID]||n>text_maximum[id-TEXT_FIRST_ID]){failure(f,"Incorrect String argument count.");return empty;}
    if(id==30)return text_value(to_text(args[0]));
    if(id==37){
        if(args[0].tag!=ARRAY||args[1].tag!=STRING){failure(f,"String.Join requires an array and a separator string.");return empty;}
        Array*a=pointer(args[0].ref);Text*sep=pointer(args[1].ref);TextBuilder b={0};
        for(u32 i=0;i<a->count;i++){if(i)tb_text(&b,sep);format_value(&b,a->values[i],1);}
        return text_value(tb_finish(&b));
    }
    if(id==44){
        Value c=args[0];
        if(c.tag!=INT||c.number<0||c.number>0x10FFFF){failure(f,"String.FromCode requires a code point 0..0x10FFFF.");return empty;}
        u32 cp=(u32)c.number;
        if(cp<0x10000){Text*t=pointer(aqua_text(1));t->data[0]=(uint16_t)cp;return text_value(t);}
        Text*t=pointer(aqua_text(2));cp-=0x10000;t->data[0]=(uint16_t)(0xD800+(cp>>10));t->data[1]=(uint16_t)(0xDC00+(cp&0x3FF));return text_value(t);
    }
    if(args[0].tag!=STRING){failure(f,"String function requires a string.");return empty;}
    Text*t=pointer(args[0].ref);
    if(id==31){
        u32 start,end;if(!text_index(f,args[1],t->length,&start)||!text_index(f,args[2],t->length,&end))return empty;
        if(start>end){failure(f,"String slice start must not exceed its end.");return empty;}
        return text_value(text_slice(t,start,end));
    }
    if(id>=32&&id<=36){
        if(args[1].tag!=STRING){failure(f,"String function requires a string argument.");return empty;}
        Text*s=pointer(args[1].ref);
        if(id==32)return number(text_find(t,s,0),INT);
        if(id==33)return value(BOOL,0,text_find(t,s,0)>=0);
        if(id==34)return value(BOOL,0,text_at(t,0,s));
        if(id==35)return value(BOOL,0,s->length<=t->length&&text_at(t,t->length-s->length,s));
        /* Split: an empty separator splits into code points. */
        u32 count=0;
        if(!s->length){for(u32 i=0;i<t->length;i++){count++;if(t->data[i]>=0xD800&&t->data[i]<=0xDBFF&&i+1<t->length&&t->data[i+1]>=0xDC00&&t->data[i+1]<=0xDFFF)i++;}}
        else{count=1;for(int at=text_find(t,s,0);at>=0;at=text_find(t,s,(u32)at+s->length))count++;}
        if(count>ARRAY_LIMIT){failure(f,"String.Split result exceeds the Wasm array limit.");return empty;}
        Array*a=array(count);u32 index=0,start=0;
        if(!s->length){
            for(u32 i=0;i<t->length;i++){u32 width=1;if(t->data[i]>=0xD800&&t->data[i]<=0xDBFF&&i+1<t->length&&t->data[i+1]>=0xDC00&&t->data[i+1]<=0xDFFF)width=2;a->values[index++]=text_value(text_slice(t,i,i+width));i+=width-1;}
        } else {
            for(int at=text_find(t,s,0);at>=0;at=text_find(t,s,start)){a->values[index++]=text_value(text_slice(t,start,(u32)at));start=(u32)at+s->length;}
            a->values[index++]=text_value(text_slice(t,start,t->length));
        }
        return value(ARRAY,address(a),0);
    }
    if(id==38){
        if(args[1].tag!=STRING||args[2].tag!=STRING){failure(f,"String.Replace requires strings.");return empty;}
        Text*old=pointer(args[1].ref),*replacement=pointer(args[2].ref);
        if(!old->length){failure(f,"String.Replace requires a non-empty search string.");return empty;}
        TextBuilder b={0};u32 start=0;
        for(int at=text_find(t,old,0);at>=0;at=text_find(t,old,start)){tb_units(&b,t->data+start,(u32)at-start);tb_text(&b,replacement);start=(u32)at+old->length;}
        tb_units(&b,t->data+start,t->length-start);return text_value(tb_finish(&b));
    }
    if(id==39){
        u32 start=0,end=t->length;while(start<end&&text_space(t->data[start]))start++;while(end>start&&text_space(t->data[end-1]))end--;
        return text_value(text_slice(t,start,end));
    }
    if(id==40||id==41){
        Text*s=pointer(aqua_text(t->length));for(u32 i=0;i<t->length;i++)s->data[i]=text_case(t->data[i],id==40);return text_value(s);
    }
    if(id==42){
        Value c=args[1];
        if(c.tag!=INT||c.number<0){failure(f,"String.Repeat count must be a nonnegative integer.");return empty;}
        u32 times=(u32)c.number;
        if(t->length&&times>ARRAY_LIMIT/t->length){failure(f,"String.Repeat result is too large.");return empty;}
        Text*s=pointer(aqua_text(t->length*times));for(u32 i=0;i<times;i++)memcpy(s->data+i*t->length,t->data,t->length*2);return text_value(s);
    }
    if(id==43){
        Value at=args[1];
        if(at.tag!=INT){failure(f,"String.Code index must be an integer.");return empty;}
        if(at.number<0||at.number>=t->length)return value(NULL_VALUE,0,0);
        u32 i=(u32)at.number;u32 c=t->data[i];
        if(c>=0xD800&&c<=0xDBFF&&i+1<t->length&&t->data[i+1]>=0xDC00&&t->data[i+1]<=0xDFFF)c=0x10000+((c-0xD800)<<10)+(t->data[i+1]-0xDC00);
        return number(c,INT);
    }
    /* id 45: ParseInt -> integer, or null when the text is not a 32-bit decimal integer. */
    u32 i=0,end=t->length;while(i<end&&text_space(t->data[i]))i++;while(end>i&&text_space(t->data[end-1]))end--;
    int negative=0;if(i<end&&(t->data[i]=='+'||t->data[i]=='-')){negative=t->data[i]=='-';i++;}
    if(i>=end)return value(NULL_VALUE,0,0);
    double total=0;
    for(;i<end;i++){uint16_t c=t->data[i];if(c<'0'||c>'9')return value(NULL_VALUE,0,0);total=total*10+(c-'0');if(total>2147483648.0)return value(NULL_VALUE,0,0);}
    if(negative)total=-total;
    if(total>2147483647.0)return value(NULL_VALUE,0,0);
    return number(total,INT);
}
