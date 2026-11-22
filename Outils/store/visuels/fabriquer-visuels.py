from PIL import Image, ImageDraw, ImageFont, ImageFilter
import math
F='/usr/share/fonts/opentype/inter/'
def font(w,s): return ImageFont.truetype(F+{'b':'Inter-Bold.otf','sb':'Inter-SemiBold.otf','m':'Inter-Medium.otf','r':'Inter-Regular.otf'}[w],s)
C1=(128,97,246); C2=(69,125,246)

def gradient(w,h,c1,c2):
    g=Image.new('RGB',(w,h)); px=g.load()
    for y in range(h):
        for x in range(w):
            t=(x+y)/(w+h-2); px[x,y]=tuple(int(c1[i]+(c2[i]-c1[i])*t) for i in range(3))
    return g

def icon(size):
    S=4; n=size*S
    g=gradient(256,256,C1,C2).resize((n,n),Image.BICUBIC)
    mask=Image.new('L',(n,n),0); ImageDraw.Draw(mask).rounded_rectangle((0,0,n-1,n-1),radius=int(n*0.22),fill=255)
    im=Image.new('RGBA',(n,n),(0,0,0,0)); im.paste(g,(0,0),mask)
    d=ImageDraw.Draw(im)
    a=int(n*0.205); gap=int(n*0.06); r=int(a*0.28)
    x0=(n-(2*a+gap))//2; y0=x0
    for i,(cx,cy) in enumerate([(x0,y0),(x0+a+gap,y0),(x0,y0+a+gap),(x0+a+gap,y0+a+gap)]):
        if i<3: d.rounded_rectangle((cx,cy,cx+a,cy+a),radius=r,fill='white')
        else: d.ellipse((cx,cy,cx+a,cy+a),fill='white')
    return im.resize((size,size),Image.LANCZOS)

def background(w,h):
    bg=Image.new('RGB',(w,h),(14,15,30))
    glow=Image.new('RGB',(w,h),(0,0,0)); d=ImageDraw.Draw(glow)
    d.ellipse((w*0.05,h*0.02,w*0.95,h*0.62 if h>w else h*0.9),fill=(70,55,170))
    glow=glow.filter(ImageFilter.GaussianBlur(min(w,h)*0.18))
    return Image.blend(bg,Image.composite(glow,bg,Image.new('L',(w,h),255)),0.55)

def shadow(base,ic,pos,blur):
    sh=Image.new('RGBA',base.size,(0,0,0,0)); m=ic.split()[3]
    tmp=Image.new('RGBA',ic.size,(20,10,60,170)); sh.paste(tmp,(pos[0],pos[1]+int(ic.size[1]*0.05)),m)
    sh=sh.filter(ImageFilter.GaussianBlur(blur)); base.alpha_composite(sh)

def center(d,y,text,f,fill,W):
    w=d.textlength(text,font=f); d.text(((W-w)/2,y),text,font=f,fill=fill)

def pill(d,cx,y,text,f,W):
    tw=d.textlength(text,font=f); ph=int(f.size*2.1); pw=tw+f.size*2.2
    x=cx-pw/2; d.rounded_rectangle((x,y,x+pw,y+ph),radius=ph//2,fill=(36,38,66),outline=(92,84,170),width=3)
    d.text((x+f.size*1.1,y+(ph-f.size*1.2)/2),text,font=f,fill=(232,232,245))
    return ph

# Affiche 9:16
W,H=1440,2160
im=background(W,H).convert('RGBA')
ic=icon(620); p=((W-620)//2,330); shadow(im,ic,p,40); im.alpha_composite(ic,p)
d=ImageDraw.Draw(im)
center(d,1060,'Centre PC',font('b',190),'white',W)
center(d,1300,'L’entretien de votre PC Windows,',font('m',66),(205,208,235),W)
center(d,1390,'en un seul endroit',font('m',66),(205,208,235),W)
f=font('sb',52); y=1560
for t in ['Mises à jour en un clic','Bureau rangé, bilan du PC']:
    y+=pill(d,W/2,y,t,f,W)+34
center(d,1985,'Gratuit · Sans publicité · En français',font('m',50),(160,165,205),W)
im.convert('RGB').save('/home/claude/visuels/CentrePC-affiche-9x16.png',optimize=True)

# Zone 1:1
W=H=2160
im=background(W,H).convert('RGBA')
ic=icon(900); p=((W-900)//2,330); shadow(im,ic,p,50); im.alpha_composite(ic,p)
d=ImageDraw.Draw(im)
center(d,1360,'Centre PC',font('b',250),'white',W)
center(d,1660,'L’entretien de votre PC Windows,',font('m',92),(205,208,235),W)
center(d,1780,'en un seul endroit',font('m',92),(205,208,235),W)
center(d,1960,'Gratuit · Sans publicité · En français',font('m',68),(160,165,205),W)
im.convert('RGB').save('/home/claude/visuels/CentrePC-zone-1x1.png',optimize=True)
print('ok')
